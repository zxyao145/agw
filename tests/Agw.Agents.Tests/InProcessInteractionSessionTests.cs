using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class InProcessInteractionSessionTests
{
    [Fact]
    public async Task ResolveAsync_ResponseDuringPublication_CompletesMatchingRequest()
    {
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(sink);
        sink.OnWrite = async (message, token) =>
            Assert.True(
                await session.TrySubmitAsync(
                    InteractionTestData.Decision(InteractionTestData.Read(message), true, "looks good"),
                    token
                )
            );
        var result = await session.ResolveAsync(
            InteractionTestData.Gate("gate"),
            TestContext.Current.CancellationToken
        );
        var decision = Assert.IsType<WorkflowGateDecision>(
            Assert.IsType<InteractionResolution.Resolved>(result).Response
        );
        Assert.True(decision.Approved);
        Assert.Equal("looks good", decision.ResponseText);
        Assert.False(await session.TrySubmitAsync(decision, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_PublicationFails_RemovesPendingRequest()
    {
        var waiting = 0;
        var failure = new IOException("send failed");
        var sink = new InteractionTestSink { OnWrite = (_, _) => ValueTask.FromException(failure) };
        var session = new InProcessInteractionSession(sink, pendingCountChanged: count => waiting = count);
        var request = InteractionTestData.Gate("gate");
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<IOException>(async () =>
                await session.ResolveAsync(request, TestContext.Current.CancellationToken)
            )
        );
        Assert.Equal(0, waiting);
        Assert.False(
            await session.TrySubmitAsync(
                InteractionTestData.Decision(request, true),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ResolveAsync_CancelledRequest_LeavesOtherWaitersAvailable()
    {
        var waiting = 0;
        var session = new InProcessInteractionSession(
            new InteractionTestSink(),
            pendingCountChanged: count => waiting = count
        );
        using var cancellation = new CancellationTokenSource();
        var first = session.ResolveAsync(InteractionTestData.Gate("first"), cancellation.Token).AsTask();
        var second = session
            .ResolveAsync(InteractionTestData.Gate("second"), TestContext.Current.CancellationToken)
            .AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, waiting);
        Assert.True(
            await session.TrySubmitAsync(
                InteractionTestData.Decision(InteractionTestData.Gate("second"), false),
                TestContext.Current.CancellationToken
            )
        );
        await second;
        Assert.Equal(0, waiting);
    }

    [Fact]
    public async Task TrySubmitAsync_WrongResponseKind_DoesNotConsumeRequest()
    {
        var session = new InProcessInteractionSession(new InteractionTestSink());
        var request = InteractionTestData.Gate("gate");
        var pending = session.ResolveAsync(request, TestContext.Current.CancellationToken).AsTask();
        await Assert.ThrowsAsync<AgwException>(async () =>
            await session.TrySubmitAsync(
                new ToolApprovalDecision { InteractionId = "gate", Approved = true },
                TestContext.Current.CancellationToken
            )
        );
        Assert.False(pending.IsCompleted);
        await session.TrySubmitAsync(
            InteractionTestData.Decision(request, true),
            TestContext.Current.CancellationToken
        );
        await pending;
    }

    [Fact]
    public async Task ActiveTurn_TrySubmitHumanResponseAsync_ForwardsTypedDecision()
    {
        using var cancellation = new CancellationTokenSource();
        var decision = InteractionTestData.Decision(InteractionTestData.Gate("gate"), true);
        InteractionResponse? forwarded = null;
        var turn = new ActiveTurn(
            Task.CompletedTask,
            cancellation,
            submitHumanResponseAsync: (response, _) =>
            {
                forwarded = response;
                return ValueTask.FromResult(true);
            }
        );
        Assert.True(await turn.TrySubmitHumanResponseAsync(decision, TestContext.Current.CancellationToken));
        Assert.Same(decision, forwarded);
    }

    [Fact]
    public async Task ResolveAsync_InteractionDisallowed_DeclinesToolApprovalWithoutPublishing()
    {
        var waiting = -1;
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(
            sink,
            pendingCountChanged: count => waiting = count,
            allowInteraction: false
        );

        var result = await session.ResolveAsync(
            InteractionTestData.Tool("tool"),
            TestContext.Current.CancellationToken
        );

        var decision = Assert.IsType<ToolApprovalDecision>(
            Assert.IsType<InteractionResolution.Resolved>(result).Response
        );
        Assert.False(decision.Approved);
        Assert.Equal(ApprovalScope.Once, decision.Scope);
        Assert.Empty(sink.Messages);
        Assert.Equal(-1, waiting);
    }

    [Fact]
    public async Task ResolveAsync_InteractionDisallowed_CancelsUserInputAndGate()
    {
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(sink, allowInteraction: false);

        var input = await session.ResolveAsync(
            InteractionTestData.Input("input"),
            TestContext.Current.CancellationToken
        );
        var gate = await session.ResolveAsync(InteractionTestData.Gate("gate"), TestContext.Current.CancellationToken);

        Assert.True(
            Assert.IsType<UserInputResponse>(Assert.IsType<InteractionResolution.Resolved>(input).Response).Cancelled
        );
        Assert.False(
            Assert.IsType<WorkflowGateDecision>(Assert.IsType<InteractionResolution.Resolved>(gate).Response).Approved
        );
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task ResolveAsync_InteractionDisallowedWithFullAccess_DeclinesTool()
    {
        // 自动决定在 Agent 管线内完成；到达即时通道的工具审批都需要用户。
        // Automatic decisions happen in the Agent pipeline; every tool approval reaching the live channel needs the user.
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(sink, AgwPermissionMode.FullAccess, allowInteraction: false);

        var result = await session.ResolveAsync(
            InteractionTestData.Tool("tool"),
            TestContext.Current.CancellationToken
        );

        Assert.False(
            Assert.IsType<ToolApprovalDecision>(Assert.IsType<InteractionResolution.Resolved>(result).Response).Approved
        );
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task RequestAsync_InteractionDisallowed_ReturnsCancelledResponse()
    {
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(sink, allowInteraction: false);
        var request = InteractionTestData.Input("input");

        var response = await session.RequestAsync(
            new UserInputRequest(request.InputKind, request.Prompt, request.Payload) { Source = request.Source },
            TestContext.Current.CancellationToken
        );

        Assert.True(response.Cancelled);
        Assert.Empty(sink.Messages);
    }
}
