using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class HumanInteractionRegressionTests
{
    [Theory]
    [InlineData("node-a", "call-b")]
    [InlineData("node-b", "call-a")]
    [InlineData("node-a", null)]
    public async Task RequestAsync_DifferentCallWithSingleSavedAnswer_RejectsMismatch(string node, string? callId)
    {
        var saved = InteractionTestData.Input("saved", "node-a", "call-a");
        var channel = new ResolvedHumanInteractionChannel([
            new(
                saved,
                new UserInputResponse
                {
                    InteractionId = "saved",
                    Cancelled = false,
                    ResponseData = JsonSerializer.SerializeToElement(new { answer = "A" }),
                }
            ),
        ]);
        var request = new UserInputRequest(saved.InputKind, saved.Prompt, saved.Payload)
        {
            Source = saved.Source with { NodeId = node, CallId = callId },
        };
        await Assert.ThrowsAsync<AgwException>(async () =>
            await channel.RequestAsync(request, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task RequestAsync_ExactLogicalCall_PreservesSavedInteractionId()
    {
        var saved = InteractionTestData.Input("saved", "node-a", "call-a");
        var answer = new UserInputResponse { InteractionId = "saved", Cancelled = true };
        var channel = new ResolvedHumanInteractionChannel([new(saved, answer)]);
        var response = await channel.RequestAsync(
            new UserInputRequest(saved.InputKind, saved.Prompt, saved.Payload) { Source = saved.Source },
            TestContext.Current.CancellationToken
        );
        Assert.Same(answer, response);
    }

    [Fact]
    public async Task SubmitAsync_AnotherRequestIsPending_PreservesWaitingState()
    {
        var waiting = 0;
        var session = new InProcessInteractionSession(
            new InteractionTestSink(),
            pendingCountChanged: count => waiting = count
        );
        var first = session
            .ResolveAsync(InteractionTestData.Gate("first"), TestContext.Current.CancellationToken)
            .AsTask();
        var second = session
            .ResolveAsync(InteractionTestData.Gate("second"), TestContext.Current.CancellationToken)
            .AsTask();
        await session.TrySubmitAsync(
            InteractionTestData.Decision(InteractionTestData.Gate("first"), true),
            TestContext.Current.CancellationToken
        );
        await first;
        Assert.Equal(1, waiting);
        Assert.False(second.IsCompleted);
        session.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
}
