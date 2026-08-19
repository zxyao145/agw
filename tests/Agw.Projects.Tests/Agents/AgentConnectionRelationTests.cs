using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentConnectionRelationTests
{
    [Fact]
    public async Task CreateAgentAsync_WhenConnectionIdsContainDuplicates_PersistsDistinctExistingConnections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken);
        var agent = CreateAgent(Guid.CreateVersion7());

        var created = await scope.Service.CreateAgentAsync(
            agent,
            mcpToolServerIds: null,
            skillIds: null,
            connectionIds: [scope.FirstConnectionId, scope.FirstConnectionId, Guid.Empty, Guid.CreateVersion7()],
            user: "tester"
        );

        Assert.NotNull(created);
        await using var assertContext = scope.CreateDbContext();
        var relation = Assert.Single(
            await assertContext
                .AgentConnectionRelations.Where(relation => relation.AgentId == agent.Id)
                .ToListAsync(cancellationToken)
        );
        Assert.Equal(scope.FirstConnectionId, relation.ConnectionId);
    }

    [Fact]
    public async Task UpdateAgentAsync_WhenSystemConnectionIdsChange_ReplacesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken, AgentType.System);

        var updated = await scope.Service.UpdateAgentAsync(
            scope.AgentId,
            new AgentUpdateRequest
            {
                DisplayName = "Updated agent",
                Description = "desc",
                SystemPrompt = "prompt",
                ModelProviderId = scope.ModelProviderId,
                ConnectionIds = [scope.SecondConnectionId],
            }.ToCommand(),
            "tester"
        );

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        var relation = Assert.Single(await assertContext.AgentConnectionRelations.ToListAsync(cancellationToken));
        Assert.Equal(scope.SecondConnectionId, relation.ConnectionId);
    }

    [Fact]
    public async Task ListAndGetAgentAsync_WhenAgentHasConnection_ReturnsIncludedRelation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken);

        var listed = Assert.Single(await scope.Service.ListAgentsAsync(), agent => agent.Id == scope.AgentId);
        var fetched = await scope.Service.GetAgentAsync(scope.AgentId);

        Assert.Equal(scope.FirstConnectionId, Assert.Single(listed.AgentConnectionRelations).ConnectionId);
        Assert.NotNull(fetched);
        Assert.Equal(scope.FirstConnectionId, Assert.Single(fetched.AgentConnectionRelations).ConnectionId);
    }

    private static Agent CreateAgent(Guid id, AgentType type = AgentType.External, Guid? modelProviderId = null) =>
        new()
        {
            Id = id,
            Name = $"agent-{id:N}",
            DisplayName = "Agent",
            Description = "desc",
            SystemPrompt = "prompt",
            Type = type,
            ModelProviderId = modelProviderId,
        };

    private static Connection CreateConnection(Guid id, string alias) =>
        new()
        {
            Id = id,
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth",
            DisplayName = alias,
            Alias = alias,
            Status = ConnectionStatus.Ready,
        };

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _serviceContext;

        private TestScope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            AgwDbContext serviceContext,
            AgentAppService service
        )
        {
            _connection = connection;
            _options = options;
            _serviceContext = serviceContext;
            Service = service;
        }

        public AgentAppService Service { get; }
        public Guid AgentId { get; private init; }
        public Guid FirstConnectionId { get; private init; }
        public Guid SecondConnectionId { get; private init; }
        public Guid ModelProviderId { get; private init; }

        public static async Task<TestScope> CreateAsync(
            CancellationToken cancellationToken,
            AgentType agentType = AgentType.External
        )
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            var agentId = Guid.CreateVersion7();
            var firstConnectionId = Guid.CreateVersion7();
            var secondConnectionId = Guid.CreateVersion7();
            var modelId = Guid.CreateVersion7();
            var providerId = Guid.CreateVersion7();
            var modelProviderId = Guid.CreateVersion7();
            await using (var seedContext = new AgwDbContext(options))
            {
                seedContext.Models.Add(new AgwAiModel { Id = modelId, Name = "test-model" });
                seedContext.Providers.Add(
                    new Provider
                    {
                        Id = providerId,
                        Name = "test-provider",
                        Endpoint = "https://example.test",
                    }
                );
                seedContext.ModelProviders.Add(
                    new ModelProviderRelation
                    {
                        Id = modelProviderId,
                        ModelId = modelId,
                        ProviderId = providerId,
                    }
                );
                seedContext.Agents.Add(CreateAgent(agentId, agentType, modelProviderId));
                seedContext.Connections.AddRange(
                    CreateConnection(firstConnectionId, "work-github"),
                    CreateConnection(secondConnectionId, "personal-github")
                );
                seedContext.AgentConnectionRelations.Add(
                    new AgentConnectionRelation { AgentId = agentId, ConnectionId = firstConnectionId }
                );
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            var serviceContext = new AgwDbContext(options);
            return new TestScope(connection, options, serviceContext, CreateService(serviceContext))
            {
                AgentId = agentId,
                FirstConnectionId = firstConnectionId,
                SecondConnectionId = secondConnectionId,
                ModelProviderId = modelProviderId,
            };
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await _serviceContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static AgentAppService CreateService(AgwDbContext dbContext) =>
            new(
                new EfRepository<Agent>(dbContext),
                new EfRepository<AgentConnectionRelation>(dbContext),
                new EfRepository<Connection>(dbContext),
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<AgwAiModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                new EfRepository<McpServer>(dbContext),
                new EfRepository<AgentMcpServerRelation>(dbContext),
                new EfRepository<Skill>(dbContext),
                new EfRepository<AgentSkillRelation>(dbContext),
                dbContext,
                new AgentDomainService(TimeProvider.System)
            );
    }
}
