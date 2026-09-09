using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class InteractionRulesTests
{
    [Theory]
    [InlineData(AgwPermissionMode.AlwaysAsk, ApprovalScope.Once)]
    [InlineData(AgwPermissionMode.AllowSameArguments, ApprovalScope.AlwaysArguments)]
    [InlineData(AgwPermissionMode.FullAccess, ApprovalScope.AlwaysTool)]
    public void ValidateAndNormalize_ApprovedTool_EnforcesMode(AgwPermissionMode mode, ApprovalScope scope)
    {
        var request = InteractionTestData.Tool("tool");
        var result = Assert.IsType<ToolApprovalDecision>(
            InteractionRules.ValidateAndNormalize(
                request,
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = ApprovalScope.AlwaysTool,
                },
                mode
            )
        );
        Assert.Equal(scope, result.Scope);
    }

    [Fact]
    public async Task FullAccess_OnlyOrdinaryTools_AreAutomatic()
    {
        var handler = new UnattendedInteractionHandler(AgwPermissionMode.FullAccess);
        var result = await handler.ResolveAsync(
            InteractionTestData.Tool("tool"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            ApprovalScope.AlwaysTool,
            Assert.IsType<ToolApprovalDecision>(Assert.IsType<InteractionResolution.Resolved>(result).Response).Scope
        );
        foreach (
            var request in new InteractionRequest[]
            {
                InteractionTestData.Gate("gate"),
                InteractionTestData.Input("input"),
            }
        )
            await Assert.ThrowsAsync<AgwException>(async () =>
                await handler.ResolveAsync(request, TestContext.Current.CancellationToken)
            );
    }

    [Fact]
    public async Task SetPermissionMode_FullAccess_OnlyCompletesOrdinaryTools()
    {
        var waiting = 0;
        var session = new InProcessInteractionSession(
            new InteractionTestSink(),
            AgwPermissionMode.AlwaysAsk,
            count => waiting = count
        );
        var tool = session
            .ResolveAsync(InteractionTestData.Tool("tool"), TestContext.Current.CancellationToken)
            .AsTask();
        var input = session
            .ResolveAsync(InteractionTestData.Input("input"), TestContext.Current.CancellationToken)
            .AsTask();
        var gate = session
            .ResolveAsync(InteractionTestData.Gate("gate"), TestContext.Current.CancellationToken)
            .AsTask();
        session.SetPermissionMode(AgwPermissionMode.FullAccess);
        Assert.IsType<ToolApprovalDecision>(Assert.IsType<InteractionResolution.Resolved>(await tool).Response);
        Assert.Equal(2, waiting);
        Assert.False(input.IsCompleted);
        Assert.False(gate.IsCompleted);
        session.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public void ValidateAndNormalize_UnknownScope_RejectsResponse()
    {
        Assert.Throws<AgwException>(() =>
            InteractionRules.ValidateAndNormalize(
                InteractionTestData.Tool("tool"),
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = (ApprovalScope)999,
                },
                null
            )
        );
    }
}
