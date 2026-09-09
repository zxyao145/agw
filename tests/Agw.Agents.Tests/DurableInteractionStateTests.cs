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
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var id = await RegisterExecutionAsync(database, store);
        await store.SetPermissionModeAsync(id, "user-id", mode, TestContext.Current.CancellationToken);
        await WaitForInteractionsAsync(store, id, [InteractionTestData.Tool("tool")]);
        var saved = await store.SubmitHumanResponseAsync(
            new(
                id,
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = ApprovalScope.AlwaysTool,
                }
            ),
            "user-id",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(expected, Assert.IsType<ToolApprovalDecision>(Assert.Single(saved.Responses)).Scope);
    }

    [Fact]
    public async Task SetPermissionModeAsync_MixedPending_OnlyApprovesOrdinaryTools()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var id = await RegisterExecutionAsync(database, store);
        await WaitForInteractionsAsync(
            store,
            id,
            [InteractionTestData.Tool("tool"), InteractionTestData.Input("input"), InteractionTestData.Gate("gate")]
        );
        var result = await store.SetPermissionModeAsync(
            id,
            "user-id",
            AgwPermissionMode.FullAccess,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionStatus.WaitingForHuman, result.Status);
        Assert.Equal(2, result.GetUnansweredInteractions().Count);
        Assert.Equal("tool", Assert.Single(result.Responses).InteractionId);
        Assert.IsType<ToolApprovalDecision>(result.Responses[0]);
        await Assert.ThrowsAsync<AgwException>(() =>
            store.SetPermissionModeAsync(
                id,
                "foreign-owner",
                AgwPermissionMode.AlwaysAsk,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task SetPermissionModeAsync_Running_PreservesWorkerAndAppliesToNewBoundary()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var id = await RegisterExecutionAsync(database, store);
        var running = Assert.IsType<DurableExecutionSnapshot>(
            await store.TryBeginSegmentAsync(id, DateTimeOffset.MaxValue, token)
        );
        var updated = await store.SetPermissionModeAsync(id, "user-id", AgwPermissionMode.FullAccess, token);
        Assert.Equal(running.StateVersion, updated.StateVersion);
        Assert.Equal(1, updated.Manifest.Settings.PermissionVersion);
        var saved = await store.SaveSegmentResultAsync(
            new()
            {
                ExecutionId = id,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [InteractionTestData.Tool("late-tool")],
            },
            running.StateVersion,
            token
        );
        Assert.Equal(DurableExecutionStatus.Resuming, saved.Status);
        Assert.Equal(
            ApprovalScope.AlwaysTool,
            Assert.IsType<ToolApprovalDecision>(Assert.Single(saved.Responses)).Scope
        );
    }

    [Fact]
    public async Task SaveSegmentResultAsync_NextApproval_PreservesUnconsumedInputAnswersAndCatalog()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var id = await RegisterExecutionAsync(database, store);
        var first = InteractionTestData.Input("a");
        var second = InteractionTestData.Input("b");
        await WaitForInteractionsAsync(store, id, [first], [first, second]);
        await store.SubmitHumanResponseAsync(
            new(
                id,
                new UserInputResponse
                {
                    InteractionId = "a",
                    Cancelled = false,
                    ResponseData = JsonSerializer.SerializeToElement(new { answer = "A" }),
                }
            ),
            "user-id",
            token
        );
        var running = Assert.IsType<DurableExecutionSnapshot>(
            await store.TryBeginSegmentAsync(id, DateTimeOffset.MaxValue, token)
        );
        var next = await store.SaveSegmentResultAsync(
            new()
            {
                ExecutionId = id,
                SegmentIndex = 1,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [second],
                InputCatalog = running.InputCatalog,
            },
            running.StateVersion,
            token
        );
        Assert.Empty(next.Responses);
        Assert.Equal("a", Assert.Single(next.ResolvedInputs).Request.InteractionId);
        Assert.Equal(2, next.InputCatalog.Count);
        var answered = await store.SubmitHumanResponseAsync(
            new(id, new UserInputResponse { InteractionId = "b", Cancelled = true }),
            "user-id",
            token
        );
        var resume = answered.CreateSegmentInput();
        Assert.Equal("b", Assert.Single(resume.ResolvedInteractions).Request.InteractionId);
        Assert.Equal(["a", "b"], resume.ResolvedInputs.Select(item => item.Request.InteractionId));
    }

    private static async Task WaitForInteractionsAsync(
        DurableExecutionStore store,
        Guid id,
        IReadOnlyList<InteractionRequest> requests,
        IReadOnlyList<UserInputInteraction>? catalog = null
    )
    {
        var token = TestContext.Current.CancellationToken;
        var running = Assert.IsType<DurableExecutionSnapshot>(
            await store.TryBeginSegmentAsync(id, DateTimeOffset.MaxValue, token)
        );
        await store.SaveSegmentResultAsync(
            new()
            {
                ExecutionId = id,
                SegmentIndex = running.SegmentIndex,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = requests,
                InputCatalog = catalog ?? [],
            },
            running.StateVersion,
            token
        );
    }
}

public class InteractionPermissionVersionTests
{
    [Fact]
    public void Synchronize_ModeReturnsToOriginal_RevokesPreviousGrant()
    {
        var permissions = new InteractionPermissionState(AgwPermissionMode.AllowSameArguments);
        var session = new PermissionSession();
        var call = new FunctionCallContent("call", "tool", new Dictionary<string, object?> { ["path"] = "a" });
        MafSessionApprovalState.Synchronize(session, permissions);
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, permissions.Current);
        permissions.Set(AgwPermissionMode.AlwaysAsk, 1);
        permissions.Set(AgwPermissionMode.AllowSameArguments, 2);
        MafSessionApprovalState.Synchronize(session, permissions);
        Assert.True(
            session.StateBag.TryGetValue<MafSessionGrantState>(MafSessionApprovalState.GrantStateKey, out var grants)
        );
        Assert.Empty(grants!.Grants);
        Assert.Equal(2, grants.PermissionVersion);
    }

    private sealed class PermissionSession : AgentSession;
}
