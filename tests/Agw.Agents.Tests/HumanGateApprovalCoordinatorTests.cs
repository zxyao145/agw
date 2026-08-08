using System.Text.Json;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Turns;

using Microsoft.Extensions.AI;

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
        Assert.Equal("once", decision.ApprovalScope);
    }

    [Fact]
    public async Task WaitForApprovalAsync_WhenStandingRuleSubmitted_PreservesApprovalScope()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var request = new HumanGateApprovalRequest(
            "request-standing",
            "agent",
            "Agent",
            "tool-approval",
            "Allow tool?",
            []);
        var pendingDecision = coordinator.WaitForApprovalAsync(
            request,
            TestContext.Current.CancellationToken).AsTask();

        var accepted = await coordinator.TrySubmitAsync(
            new HumanResponseCommand(
                "request-standing",
                approved: true,
                approvalScope: "always-arguments"),
            TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.Equal("always-arguments", (await pendingDecision).ApprovalScope);
    }

    [Fact]
    public async Task WaitForApprovalAsync_WhenStructuredResponseSubmitted_PreservesResponseData()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var request = new HumanGateApprovalRequest(
            "request-input",
            "human-interaction",
            null,
            "interaction",
            "Choose a database",
            []);
        var pendingDecision = coordinator.WaitForApprovalAsync(
            request,
            TestContext.Current.CancellationToken).AsTask();
        var responseData = JsonSerializer.SerializeToElement(new
        {
            answers = new Dictionary<string, string> { ["Database?"] = "PostgreSQL" }
        });

        var accepted = await coordinator.TrySubmitAsync(
            new HumanResponseCommand(
                "request-input",
                approved: true,
                responseData: responseData),
            TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.Equal(
            "PostgreSQL",
            (await pendingDecision).ResponseData!.Value
                .GetProperty("answers")
                .GetProperty("Database?")
                .GetString());
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

    [Fact]
    public void CreateResponse_NullApprovalScope_UsesSingleApproval()
    {
        var request = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent(
                "call-1",
                "run_shell",
                new Dictionary<string, object?>()));
        var decision = new HumanGateApprovalDecision(
            "approval-1",
            true,
            null,
            null!);

        var response = ToolApprovalSupport.CreateResponse(request, decision);

        Assert.IsType<ToolApprovalResponseContent>(response);
    }
}
