using Agw.Agents.Runtime.Agentflows;
using Agw.Agents.Runtime.Execution;
using Agw.Agents.Runtime.Contracts;

namespace Agw.Agents.Tests;

public class HumanGateApprovalCoordinatorTests
{
    [Fact]
    public async Task WaitForApprovalAsync_WhenResponseSubmitted_ReturnsDecision()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var request = new HumanGateApprovalRequest(
            "request-1",
            "human-gate",
            "Review Gate",
            "approval",
            "Approve next step?",
            []);

        var pendingDecision = coordinator.WaitForApprovalAsync(
            request,
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(pendingDecision.IsCompleted);

        var accepted = await coordinator.TrySubmitAsync(
            new HumanResponseCommand("request-1", approved: true, responseText: "looks good"),
            TestContext.Current.CancellationToken);

        Assert.True(accepted);
        var decision = await pendingDecision;
        Assert.Equal("request-1", decision.RequestId);
        Assert.True(decision.Approved);
        Assert.Equal("looks good", decision.ResponseText);
    }

    [Fact]
    public async Task ActiveTurn_TrySubmitHumanResponseAsync_ForwardsToHandler()
    {
        using var executionCts = new CancellationTokenSource();
        var command = new HumanResponseCommand("request-2", approved: true);
        HumanResponseCommand? forwarded = null;
        var activeTurn = new ActiveTurn(
            Task.CompletedTask,
            executionCts,
            submitHumanResponseAsync: (response, _) =>
            {
                forwarded = response;
                return ValueTask.FromResult(true);
            });

        var accepted = await activeTurn.TrySubmitHumanResponseAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.Same(command, forwarded);
    }
}
