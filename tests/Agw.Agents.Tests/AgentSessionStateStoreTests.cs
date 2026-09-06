using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agents.Store;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
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
            CreateScope(),
            TestContext.Current.CancellationToken
        );

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithoutUserContext_DoesNotReadCache()
    {
        var cache = new ThrowingHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            CreateScope(),
            TestContext.Current.CancellationToken
        );

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheIsEmpty_CreatesNewSession()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            CreateScope(),
            TestContext.Current.CancellationToken
        );

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsSerializedSession_DeserializesSession()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var sessionScope = CreateScope();
        await cache.SetAsync(
            sessionScope.CacheKey,
            "{\"id\":\"cached\"}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(agent, aiAgent, sessionScope, TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.DeserializedSession, session);
        Assert.Equal(0, aiAgent.CreateSessionCallCount);
        Assert.Equal(1, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsInvalidJson_CreatesNewSession()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var sessionScope = CreateScope();
        await cache.SetAsync(
            sessionScope.CacheKey,
            "not-json",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(agent, aiAgent, sessionScope, TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenProviderRejectsSerializedSession_CreatesNewSession()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var sessionScope = CreateScope();
        await cache.SetAsync(
            sessionScope.CacheKey,
            "{\"id\":\"obsolete\"}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.System };
        var aiAgent = new TestAIAgent
        {
            DeserializeException = new InvalidOperationException("unsupported session schema"),
        };

        var session = await store.GetOrCreateAsync(agent, aiAgent, sessionScope, TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(1, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task SaveAsync_SerializesSessionIntoCache()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var aiAgent = new TestAIAgent();
        var session = await aiAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var sessionScope = CreateScope();

        await store.SaveAsync(AgentType.System, sessionScope, aiAgent, session, TestContext.Current.CancellationToken);

        var serialized = await cache.GetOrCreateAsync<string>(
            sessionScope.CacheKey,
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal("{\"id\":\"created\"}", serialized);
    }

    [Fact]
    public async Task NodeState_SaveAndLoad_UsesStructuredScope()
    {
        using var userScope = PushTestUser();
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var sessionScope = new AgentSessionStateScope(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "context-1",
            Guid.CreateVersion7(),
            "flow-a:node-a"
        );
        var writer = new TestAIAgent();
        var session = await writer.CreateSessionAsync(TestContext.Current.CancellationToken);

        await store.SaveForNodeAsync(sessionScope, writer, session, TestContext.Current.CancellationToken);

        var reader = new TestAIAgent();
        var restored = await store.GetOrCreateForNodeAsync(reader, sessionScope, TestContext.Current.CancellationToken);

        Assert.Same(reader.DeserializedSession, restored);
        Assert.Equal(1, reader.DeserializeSessionCallCount);
    }

    [Fact]
    public void Scope_DifferentAgentsAndNodes_UseDifferentCacheKeys()
    {
        using var userScope = PushTestUser();
        var projectConversationId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var firstAgentId = Guid.CreateVersion7();
        var secondAgentId = Guid.CreateVersion7();

        var standalone = new AgentSessionStateScope(projectConversationId, projectId, "context-1", firstAgentId);
        var firstNode = new AgentSessionStateScope(
            projectConversationId,
            projectId,
            "context-1",
            firstAgentId,
            "flow-a:node-a"
        );
        var secondNode = new AgentSessionStateScope(
            projectConversationId,
            projectId,
            "context-1",
            firstAgentId,
            "flow-a:node-b"
        );
        var secondAgent = new AgentSessionStateScope(projectConversationId, projectId, "context-1", secondAgentId);

        Assert.Equal(
            4,
            new[] { standalone.CacheKey, firstNode.CacheKey, secondNode.CacheKey, secondAgent.CacheKey }
                .Distinct(StringComparer.Ordinal)
                .Count()
        );
    }

    [Fact]
    public async Task SaveAsync_PersistsSeparateScopesAndSerializesConcurrentInsert()
    {
        using var userScope = PushTestUser();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddDbContext<AgwDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IAgentSessionStatePersistence, AgentSessionStatePersistence>();
        await using var serviceProvider = services.BuildServiceProvider();

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        await using (var serviceScope = serviceProvider.CreateAsyncScope())
        {
            var dbContext = serviceScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            dbContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "session-state-project",
                    CreateBy = "tester",
                }
            );
            dbContext.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = "session-state-agent",
                    DisplayName = "Session State Agent",
                    CreateBy = "tester",
                }
            );
            dbContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = projectConversationId,
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Context",
                    CreateBy = "tester",
                }
            );
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var store = new AgentSessionStateStore(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AgentSessionStateStore>.Instance
        );
        var aiAgent = new TestAIAgent();
        var session = await aiAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var standaloneScope = new AgentSessionStateScope(projectConversationId, projectId, "context-1", agentId);
        var nodeScope = new AgentSessionStateScope(
            projectConversationId,
            projectId,
            "context-1",
            agentId,
            "flow-a:node-a"
        );
        var concurrentScope = new AgentSessionStateScope(
            projectConversationId,
            projectId,
            "context-1",
            agentId,
            "flow-a:concurrent"
        );

        await store.SaveAsync(
            AgentType.System,
            standaloneScope,
            aiAgent,
            session,
            TestContext.Current.CancellationToken
        );
        await store.SaveAsync(AgentType.System, nodeScope, aiAgent, session, TestContext.Current.CancellationToken);
        await Task.WhenAll(
            Enumerable
                .Range(0, 12)
                .Select(_ =>
                    store.SaveAsync(
                        AgentType.System,
                        concurrentScope,
                        aiAgent,
                        session,
                        TestContext.Current.CancellationToken
                    )
                )
        );

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var entries = await verificationContext
            .AgentSessionStates.AsNoTracking()
            .OrderBy(entry => entry.AgentflowNodeId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal(projectConversationId, entry.ProjectConversationId));
        Assert.All(entries, entry => Assert.Equal(agentId, entry.AgentId));
        Assert.Equal(
            [string.Empty, "flow-a:concurrent", "flow-a:node-a"],
            entries.Select(entry => entry.AgentflowNodeId)
        );
    }

    [Fact]
    public async Task SaveAsync_ForeignConversation_DoesNotPersistSessionState()
    {
        // Arrange
        using var userScope = PushTestUser();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddDbContext<AgwDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IAgentSessionStatePersistence, AgentSessionStatePersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        await using (var serviceScope = serviceProvider.CreateAsyncScope())
        {
            var dbContext = serviceScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            dbContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "foreign-project",
                    CreateBy = "other-user",
                }
            );
            dbContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = projectConversationId,
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Foreign context",
                    CreateBy = "other-user",
                }
            );
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var store = new AgentSessionStateStore(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AgentSessionStateStore>.Instance
        );
        var aiAgent = new TestAIAgent();
        var session = await aiAgent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        await store.SaveAsync(
            AgentType.System,
            new AgentSessionStateScope(projectConversationId, projectId, "context-1", agentId),
            aiAgent,
            session,
            TestContext.Current.CancellationToken
        );

        // Assert
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Empty(await verificationContext.AgentSessionStates.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static AgentSessionStateScope CreateScope() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "context-1", Guid.CreateVersion7());

    private static IDisposable PushTestUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "test"))
        );

    private sealed class TestAIAgent : AIAgent
    {
        public AgentSession CreatedSession { get; } = new TestAgentSession("created");
        public AgentSession DeserializedSession { get; } = new TestAgentSession("cached");
        public int CreateSessionCallCount { get; private set; }
        public int DeserializeSessionCallCount { get; private set; }
        public Exception? DeserializeException { get; init; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        {
            CreateSessionCallCount++;
            return ValueTask.FromResult(CreatedSession);
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        )
        {
            var id = Assert.IsType<TestAgentSession>(session).Id;
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { id }, jsonSerializerOptions));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        )
        {
            DeserializeSessionCallCount++;
            if (DeserializeException != null)
            {
                throw DeserializeException;
            }

            return ValueTask.FromResult(DeserializedSession);
        }

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
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Cache should not be read for external agents.");

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Cache should not be written in this test.");

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(
            IEnumerable<string> keys,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(
            IEnumerable<string> tags,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
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
            CancellationToken cancellationToken = default
        )
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
            CancellationToken cancellationToken = default
        )
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

        public override ValueTask RemoveByTagAsync(
            IEnumerable<string> tags,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
