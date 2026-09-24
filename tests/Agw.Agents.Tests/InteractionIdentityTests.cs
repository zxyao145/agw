using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class InteractionIdentityTests
{
    [Fact]
    public void Registry_SameSdkCallInDifferentNodes_KeepsDistinctStableIdentities()
    {
        var registry = new InteractionRequestRegistry();
        var request = new UserInputRequest(
            "sample",
            "Input",
            JsonSerializer.SerializeToElement(new { field = "value" })
        )
        {
            Source = new InteractionSource
            {
                NodeId = "a",
                ProviderScopeId = "port-a",
                CallId = "same-call",
                ToolName = "same-tool",
            },
        };
        var first = registry.Register("same-request", request);
        var second = registry.Register(
            "same-request",
            request with
            {
                Source = request.Source with { NodeId = "b", ProviderScopeId = "port-b" },
            }
        );
        Assert.NotEqual(first.InteractionId, second.InteractionId);
        var restored = new InteractionRequestRegistry(registry.Snapshot());
        Assert.Equal(first, restored.Find("same-request", "port-a"));
        Assert.Equal(second, restored.Find("same-request", "port-b"));
        Assert.Null(restored.Find("same-request", "missing-port"));
        Assert.Equal(first.InteractionId, restored.Register("same-request", request).InteractionId);
        Assert.Throws<AgwException>(() =>
            restored.Register(
                "same-request",
                request with
                {
                    Payload = JsonSerializer.SerializeToElement(new { field = "changed" }),
                }
            )
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replay_AmbiguousOrChangedInput_FailsClosed(bool duplicate)
    {
        var request = InteractionTestData.Input("saved", "node", "call");
        var saved = new DurableResolvedInteraction(
            request,
            new UserInputResponse { InteractionId = "saved", Cancelled = true }
        );
        var sameCall = new DurableResolvedInteraction(
            request with
            {
                InteractionId = "saved-again",
            },
            new UserInputResponse { InteractionId = "saved-again", Cancelled = true }
        );
        var replay = ExecutionTestScopes.ResolvedChannel(duplicate ? [saved, sameCall] : [saved]);
        var declaration = new UserInputRequest(
            request.InputKind,
            request.Prompt,
            duplicate ? request.Payload : JsonSerializer.SerializeToElement(new { changed = true })
        )
        {
            Source = request.Source,
        };
        await Assert.ThrowsAsync<AgwException>(async () =>
            await replay.RequestAsync(declaration, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task PendingHandler_EveryRequestKind_WaitsForPendingSet()
    {
        var handler = new PendingInteractionHandler(HumanInteractionPolicy.Allow, new InteractionRequestRegistry());

        foreach (
            var request in new InteractionRequest[]
            {
                InteractionTestData.Tool("tool"),
                InteractionTestData.Input("input"),
                InteractionTestData.Gate("gate"),
            }
        )
        {
            var result = await handler.ResolveAsync(request, TestContext.Current.CancellationToken);
            Assert.Same(request, Assert.IsType<InteractionResolution.Pending>(result).Request);
        }
    }

    [Fact]
    public async Task ResolveAsync_InteractionDisallowed_DeclinesWithoutWaitingForHuman()
    {
        var handler = new PendingInteractionHandler(
            HumanInteractionPolicy.Allow,
            new InteractionRequestRegistry(),
            allowInteraction: false
        );

        var approval = await handler.ResolveAsync(
            InteractionTestData.Tool("tool"),
            TestContext.Current.CancellationToken
        );
        var input = await handler.ResolveAsync(
            InteractionTestData.Input("input"),
            TestContext.Current.CancellationToken
        );

        Assert.False(
            Assert
                .IsType<ToolApprovalDecision>(Assert.IsType<InteractionResolution.Resolved>(approval).Response)
                .Approved
        );
        Assert.True(
            Assert.IsType<UserInputResponse>(Assert.IsType<InteractionResolution.Resolved>(input).Response).Cancelled
        );
    }

    [Fact]
    public async Task ResolvedChannel_InteractionDisallowed_AnswersWithCancellation()
    {
        var channel = ExecutionTestScopes.ResolvedChannel([], allowInteraction: false);
        var request = InteractionTestData.Input("input");

        var response = await channel.RequestAsync(
            new UserInputRequest(request.InputKind, request.Prompt, request.Payload) { Source = request.Source },
            TestContext.Current.CancellationToken
        );

        Assert.True(response.Cancelled);
    }
}
