using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Theory]
    [InlineData(AgwPermissionMode.AlwaysAsk, ApprovalScope.Once)]
    [InlineData(AgwPermissionMode.AllowSameArguments, ApprovalScope.AlwaysArguments)]
    public async Task SubmitHumanResponseAsync_ToolScope_NormalizesBeforePersisting(
        AgwPermissionMode mode,
        ApprovalScope expected
    )
    {
        var id = await RegisterAsync(permissionMode: mode);
        await WaitForInteractionsAsync(id, [InteractionTestData.Tool("tool")]);
        var saved = await Store.SubmitHumanResponseAsync(
            new(
                id,
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = ApprovalScope.AlwaysTool,
                }
            ),
            UserId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(expected, Assert.IsType<ToolApprovalDecision>(Assert.Single(saved.Responses)).Scope);
    }

    [Fact]
    public async Task SetPermissionModeAsync_MixedPending_DoesNotResolveCurrentTurnInteractions()
    {
        var id = await RegisterAsync();
        await WaitForInteractionsAsync(
            id,
            [InteractionTestData.Tool("tool"), InteractionTestData.Input("input"), InteractionTestData.Gate("gate")]
        );
        var result = await Store.SetPermissionModeAsync(
            id,
            UserId,
            AgwPermissionMode.FullAccess,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionStatus.WaitingForHuman, result.Status);
        Assert.Equal(3, result.GetUnansweredInteractions().Count);
        Assert.Empty(result.Responses);
        Assert.Null(result.Manifest.Settings.PermissionMode);
        Assert.Equal(AgwPermissionMode.FullAccess, result.Manifest.Settings.NextPermissionMode);
        await Assert.ThrowsAsync<AgwException>(() =>
            Store.SetPermissionModeAsync(
                id,
                "foreign-owner",
                AgwPermissionMode.AlwaysAsk,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task SetPermissionModeAsync_Running_PreservesCurrentTurnIncludingNewBoundaries()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        var running = await Store.GetAsync(id, token);
        var reconnect = Agw.Agents.Execution.Runtimes.Durable.DurableExecutionCoordinator.ToStatus(
            running with
            {
                Manifest = running.Manifest with
                {
                    Settings = running.Manifest.Settings with { PermissionVersion = 5 },
                },
            }
        );
        Assert.Equal(5, reconnect.ActivePermissionVersion);
        Assert.Equal(5, reconnect.NextPermissionVersion);
        var updated = await Store.SetPermissionModeAsync(id, UserId, AgwPermissionMode.FullAccess, token);
        Assert.Equal(running.StateVersion, updated.StateVersion);
        Assert.Equal(0, updated.Manifest.Settings.PermissionVersion);
        Assert.Equal(1, updated.Manifest.Settings.NextPermissionVersion);
        var saved = await ApplyAsync(
            lease,
            new()
            {
                ExecutionId = id,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [InteractionTestData.Tool("late-tool")],
            }
        );
        Assert.Equal(DurableExecutionStatus.WaitingForHuman, saved.Status);
        Assert.Empty(saved.Responses);
        Assert.Null(saved.Manifest.Settings.PermissionMode);
        Assert.Equal(AgwPermissionMode.FullAccess, saved.Manifest.Settings.NextPermissionMode);
    }

    [Fact]
    public async Task ApplySegmentResultAsync_NextApproval_PreservesUnconsumedInputAnswersAndCatalog()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var first = InteractionTestData.Input("a");
        var second = InteractionTestData.Input("b");
        await WaitForInteractionsAsync(id, [first], [first, second]);
        await Store.SubmitHumanResponseAsync(
            new(
                id,
                new UserInputResponse
                {
                    InteractionId = "a",
                    Cancelled = false,
                    ResponseData = JsonSerializer.SerializeToElement(new { answer = "A" }),
                }
            ),
            UserId,
            token
        );
        var lease = await ClaimAsync(id);
        var running = await Store.GetAsync(id, token);
        var next = await ApplyAsync(
            lease,
            new()
            {
                ExecutionId = id,
                SegmentIndex = 1,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [second],
                InputCatalog = running.InputCatalog,
            }
        );
        Assert.Empty(next.Responses);
        Assert.Equal("a", Assert.Single(next.ResolvedInputs).Request.InteractionId);
        Assert.Equal(2, next.InputCatalog.Count);
        var answered = await Store.SubmitHumanResponseAsync(
            new(id, new UserInputResponse { InteractionId = "b", Cancelled = true }),
            UserId,
            token
        );
        var resume = answered.CreateSegmentInput();
        Assert.Equal("b", Assert.Single(resume.ResolvedInteractions).Request.InteractionId);
        Assert.Equal(["a", "b"], resume.ResolvedInputs.Select(item => item.Request.InteractionId));
    }

    /// <summary>
    /// 领取租约并提交一个等待人工的 Segment 结果。
    /// Claims the lease and commits a segment result that waits for humans.
    /// </summary>
    private async Task WaitForInteractionsAsync(
        Guid id,
        IReadOnlyList<InteractionRequest> requests,
        IReadOnlyList<UserInputInteraction>? catalog = null
    )
    {
        var lease = await ClaimAsync(id);
        var running = await Store.GetAsync(id, TestContext.Current.CancellationToken);
        await ApplyAsync(
            lease,
            new()
            {
                ExecutionId = id,
                SegmentIndex = running.SegmentIndex,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = requests,
                InputCatalog = catalog ?? [],
            }
        );
    }
}

public class InteractionPermissionVersionTests
{
    [Fact]
    public void Synchronize_LaterTurnReturnsToOriginalMode_RevokesPreviousGrant()
    {
        var firstTurn = new InteractionPermissionState(AgwPermissionMode.AllowSameArguments);
        var session = new PermissionSession();
        var call = new FunctionCallContent("call", "tool", new Dictionary<string, object?> { ["path"] = "a" });
        MafSessionApprovalState.Synchronize(session, firstTurn);
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, firstTurn.Current);
        var laterTurn = new InteractionPermissionState(AgwPermissionMode.AllowSameArguments, version: 2);
        MafSessionApprovalState.Synchronize(session, laterTurn);
        Assert.True(
            session.StateBag.TryGetValue<MafSessionGrantState>(MafSessionApprovalState.GrantStateKey, out var grants)
        );
        Assert.Empty(grants!.Grants);
        Assert.Equal(2, grants.PermissionVersion);
    }

    private sealed class PermissionSession : AgentSession;
}
