using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Runtimes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ResourceOwningAIAgentTests
{
    [Fact]
    public async Task DisposeAsync_DisposesInnerAgentAndOwnedResourcesOnce()
    {
        var order = new List<string>();
        var inner = new DisposableAIAgent(order);
        var resource = new TrackingResource(order);
        var agent = new ResourceOwningAIAgent(inner, resource);

        await agent.DisposeAsync();
        await agent.DisposeAsync();

        Assert.Equal(["agent", "resource"], order);
    }

    [Fact]
    public async Task DisposeAsync_InnerAgentFails_StillDisposesOwnedResources()
    {
        var order = new List<string>();
        var inner = new DisposableAIAgent(order, throwOnDispose: true);
        var resource = new TrackingResource(order);
        var agent = new ResourceOwningAIAgent(inner, resource);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());

        Assert.Equal(["agent", "resource"], order);
    }

    [Fact]
    public async Task DisposeAsync_InnerAgentAndResourceFail_ReportsBothFailures()
    {
        var order = new List<string>();
        var inner = new DisposableAIAgent(order, throwOnDispose: true);
        var resource = new TrackingResource(order, throwOnDispose: true);
        var agent = new ResourceOwningAIAgent(inner, resource);

        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await agent.DisposeAsync());

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(["agent", "resource"], order);
    }

    [Fact]
    public async Task AgentRuntime_DisposeAsync_ReleasesAgentOwnedResources()
    {
        var order = new List<string>();
        var resource = new TrackingResource(order);
        var agent = new ResourceOwningAIAgent(new DisposableAIAgent(order), resource);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            new TestAgentSession(),
            Guid.CreateVersion7(),
            "context",
            sessionStateScope: null
        );

        await runtime.DisposeAsync();

        Assert.Equal(["agent", "resource"], order);
    }

    private sealed class TrackingResource : IAsyncDisposable
    {
        private readonly List<string> _order;
        private readonly bool _throwOnDispose;

        public TrackingResource(List<string> order, bool throwOnDispose = false)
        {
            _order = order;
            _throwOnDispose = throwOnDispose;
        }

        public ValueTask DisposeAsync()
        {
            _order.Add("resource");
            return _throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("resource disposal failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableAIAgent : AIAgent, IAsyncDisposable
    {
        private readonly List<string> _order;
        private readonly bool _throwOnDispose;

        public DisposableAIAgent(List<string> order, bool throwOnDispose = false)
        {
            _order = order;
            _throwOnDispose = throwOnDispose;
        }

        public ValueTask DisposeAsync()
        {
            _order.Add("agent");
            return _throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("inner disposal failed"))
                : ValueTask.CompletedTask;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            yield break;
        }
    }

    private sealed class TestAgentSession : AgentSession;
}
