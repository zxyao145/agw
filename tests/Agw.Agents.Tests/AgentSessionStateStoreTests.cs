using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Application.AgentRun;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentSessionStateStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WhenAgentIsExternal_DoesNotReadCache()
    {
        var cache = new ThrowingHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.External };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheIsEmpty_CreatesNewSession()
    {
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsSerializedSession_DeserializesSession()
    {
        var cache = new InMemoryHybridCache();
        await cache.SetAsync("task-1", "{\"id\":\"cached\"}", cancellationToken: TestContext.Current.CancellationToken);
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.DeserializedSession, session);
        Assert.Equal(0, aiAgent.CreateSessionCallCount);
        Assert.Equal(1, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsInvalidJson_CreatesNewSession()
    {
        var cache = new InMemoryHybridCache();
        await cache.SetAsync("task-1", "not-json", cancellationToken: TestContext.Current.CancellationToken);
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task SaveAsync_SerializesSessionIntoCache()
    {
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var aiAgent = new TestAIAgent();
        var session = await aiAgent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await store.SaveAsync("task-1", aiAgent, session, TestContext.Current.CancellationToken);

        var serialized = await cache.GetOrCreateAsync<string>(
            "task-1",
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("{\"id\":\"created\"}", serialized);
    }

    private sealed class TestAIAgent : AIAgent
    {
        public AgentSession CreatedSession { get; } = new TestAgentSession("created");
        public AgentSession DeserializedSession { get; } = new TestAgentSession("cached");
        public int CreateSessionCallCount { get; private set; }
        public int DeserializeSessionCallCount { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        {
            CreateSessionCallCount++;
            return ValueTask.FromResult(CreatedSession);
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
        {
            var id = Assert.IsType<TestAgentSession>(session).Id;
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { id }, jsonSerializerOptions));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
        {
            DeserializeSessionCallCount++;
            return ValueTask.FromResult(DeserializedSession);
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
    }

    private sealed class TestAgentSession(string id) : AgentSession
    {
        public string Id { get; } = id;
    }

    private sealed class ThrowingHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cache should not be read for external agents.");

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cache should not be written in this test.");

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class InMemoryHybridCache : HybridCache
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            if (_values.TryGetValue(key, out var value))
            {
                return ValueTask.FromResult((T)value!);
            }

            return CreateAsync();

            async ValueTask<T> CreateAsync()
            {
                var created = await factory(state, cancellationToken);
                _values[key] = created;
                return created;
            }
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            foreach (var key in keys)
            {
                _values.Remove(key);
            }

            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
