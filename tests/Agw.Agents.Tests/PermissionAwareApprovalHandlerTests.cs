using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class PermissionAwareApprovalHandlerTests
{
    [Fact]
    public async Task FullAccess_ToolApproval_IsAutomaticAlwaysTool()
    {
        var inner = new RecordingHandler();
        var handler = new PermissionAwareApprovalHandler(inner, PermissionMode.FullAccess);
        var request = CreateToolRequest();

        var decision = await handler.WaitForApprovalAsync(request, TestContext.Current.CancellationToken);

        Assert.False(handler.RequiresHumanResponse(request));
        Assert.True(decision.Approved);
        Assert.Equal("always-tool", decision.ApprovalScope);
        Assert.Equal(0, inner.CallCount);
    }

    [Theory]
    [InlineData(PermissionMode.AlwaysAsk, "once")]
    [InlineData(PermissionMode.AllowSameArguments, "always-arguments")]
    public async Task ManualPolicy_NormalizesApprovedScope(PermissionMode permissionMode, string expectedScope)
    {
        var inner = new RecordingHandler();
        var handler = new PermissionAwareApprovalHandler(inner, permissionMode);
        var request = CreateToolRequest();

        var decision = await handler.WaitForApprovalAsync(request, TestContext.Current.CancellationToken);

        Assert.True(handler.RequiresHumanResponse(request));
        Assert.Equal(expectedScope, decision.ApprovalScope);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task SwitchingToFullAccess_ApprovesPendingToolRequest()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var handler = new PermissionAwareApprovalHandler(coordinator, PermissionMode.AlwaysAsk);
        var request = CreateToolRequest();
        var pendingDecision = handler.WaitForApprovalAsync(request, TestContext.Current.CancellationToken).AsTask();

        handler.SetPermissionMode(PermissionMode.FullAccess);

        Assert.False(handler.RequiresHumanResponse(request));
        var decision = await pendingDecision;
        Assert.True(decision.Approved);
        Assert.Equal("always-tool", decision.ApprovalScope);
    }

    [Fact]
    public void DynamicPermissionChange_ClearsRegisteredSessionRules()
    {
        var session = new TestSession();
        var state = new PermissionModeState(PermissionMode.FullAccess);
        state.Register(session);
        session.StateBag.SetValue(ToolApprovalPermissionState.ToolApprovalStateKey, "standing-rule");

        state.Set(PermissionMode.AlwaysAsk);

        Assert.False(session.StateBag.TryGetValue<string>(ToolApprovalPermissionState.ToolApprovalStateKey, out _));
        Assert.Equal(PermissionMode.AlwaysAsk, state.Current);
    }

    [Fact]
    public void PermissionChange_ClearsPreviousStandingApprovalRules()
    {
        var session = new TestSession();
        ToolApprovalPermissionState.Apply(session, PermissionMode.FullAccess);
        session.StateBag.SetValue(ToolApprovalPermissionState.ToolApprovalStateKey, "standing-rule");

        ToolApprovalPermissionState.Apply(session, PermissionMode.FullAccess);
        Assert.True(session.StateBag.TryGetValue<string>(ToolApprovalPermissionState.ToolApprovalStateKey, out _));

        ToolApprovalPermissionState.Apply(session, PermissionMode.AlwaysAsk);
        Assert.False(session.StateBag.TryGetValue<string>(ToolApprovalPermissionState.ToolApprovalStateKey, out _));

        session.StateBag.SetValue(ToolApprovalPermissionState.ToolApprovalStateKey, "managed-rule");
        ToolApprovalPermissionState.Apply(session, permissionMode: null);
        Assert.False(session.StateBag.TryGetValue<string>(ToolApprovalPermissionState.ToolApprovalStateKey, out _));
        Assert.False(session.StateBag.TryGetValue<string>(ToolApprovalPermissionState.PermissionModeStateKey, out _));
    }

    [Fact]
    public void CreateModeStatusMessage_CreatesHiddenPersistedSnapshot()
    {
        var message = AgentRuntimeService.CreateModeStatusMessage("execute");

        Assert.Equal(ToolMessageTypes.ModeStatus, message.AdditionalProperties!["type"]);
        Assert.Equal("execute", message.AdditionalProperties["mode"]);
        Assert.Equal("control", message.AdditionalProperties["presentation"]);
    }

    [Theory]
    [InlineData("ask_user_question")]
    [InlineData("human-gate")]
    public async Task FullAccess_HumanInput_IsNotAutomaticallyApproved(string toolName)
    {
        var request =
            toolName == "human-gate"
                ? new HumanGateApprovalRequest("human", "node", null, "approval", "Approve?", [])
                : ToolApprovalSupport.CreateRequest(
                    new ToolApprovalRequestContent("ask", new FunctionCallContent("call", toolName)),
                    "node"
                );
        var handler = UnattendedApprovalHandler.Create(PermissionMode.FullAccess);

        var error = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(async () =>
            await handler.WaitForApprovalAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.True(handler.RequiresHumanResponse(request));
        Assert.Contains("unattended", error.Message);
    }

    [Fact]
    public async Task SwitchingToFullAccess_DoesNotApprovePendingQuestion()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var handler = new PermissionAwareApprovalHandler(coordinator, PermissionMode.AlwaysAsk);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = ToolApprovalSupport.CreateRequest(
            new ToolApprovalRequestContent("ask", new FunctionCallContent("call", "ask_user_question")),
            "node"
        );
        var pending = handler.WaitForApprovalAsync(request, cancellation.Token).AsTask();

        handler.SetPermissionMode(PermissionMode.FullAccess);

        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static HumanGateApprovalRequest CreateToolRequest()
    {
        var approval = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "run_shell", new Dictionary<string, object?>())
        );
        return ToolApprovalSupport.CreateRequest(approval, "standalone");
    }

    private sealed class RecordingHandler : IHumanGateApprovalHandler
    {
        public int CallCount { get; private set; }

        public ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            return ValueTask.FromResult(
                new HumanGateApprovalDecision(
                    request.RequestId,
                    Approved: true,
                    ResponseText: null,
                    ApprovalScope: "always-tool"
                )
            );
        }
    }

    private sealed class TestSession : AgentSession;
}
