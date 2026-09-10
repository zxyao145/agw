using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
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
        var replay = new ResolvedHumanInteractionChannel(duplicate ? [saved, saved] : [saved]);
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
    public async Task DurableResolution_RefreshesPermissionsBeforeAuthorizingInsideNestedScope()
    {
        var accessor = new HumanInteractionContextAccessor();
        var permissions = new InteractionPermissionState(AgwPermissionMode.FullAccess);
        var refreshed = 0;
        using var worker = accessor.Push(
            null,
            permissions: permissions,
            refreshPermissions: _ =>
            {
                refreshed++;
                permissions.Set(AgwPermissionMode.AlwaysAsk, 1);
                return ValueTask.CompletedTask;
            }
        );
        var registry = new InteractionRequestRegistry();
        using var segment = accessor.Push(new ResolvedHumanInteractionChannel([]), registry, permissions);
        var handler = new DurableInteractionHandler(
            permissions,
            HumanInteractionPolicy.Allow,
            registry,
            accessor.RefreshPermissionsAsync
        );
        var result = await handler.ResolveAsync(
            InteractionTestData.Tool("tool"),
            TestContext.Current.CancellationToken
        );
        Assert.IsType<InteractionResolution.Pending>(result);
        Assert.Equal(1, refreshed);
        Assert.Equal(AgwPermissionMode.AlwaysAsk, permissions.Current);
    }
}
