using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceAppRelationTests
{
    [Fact]
    public async Task CreateAgentAsync_WhenAppInstanceIdsProvided_PersistsAgentAppRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken);
        var service = scope.CreateAgentAppService();

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "writer-agent",
            DisplayName = "Writer Agent",
            Description = "desc",
            SystemPrompt = "prompt",
            Type = AgentType.External
        };

        var created = await service.CreateAgentAsync(
            agent,
            mcpToolServerIds: null,
            skillIds: null,
            appInstanceIds: [scope.GithubAppInstanceId, scope.GoogleAppInstanceId],
            user: "tester");

        Assert.NotNull(created);

        await using var assertContext = scope.CreateDbContext();
        var relations = await assertContext.Set<AgentAppRelation>()
            .Where(x => x.AgentId == agent.Id)
            .OrderBy(x => x.AppInstanceId)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, relations.Count);
    }

    [Fact]
    public async Task UpdateAgentAsync_WhenAppInstanceIdsChange_ReplacesAgentAppRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken);
        var service = scope.CreateAgentAppService();

        var updated = await service.UpdateAgentAsync(
            scope.AgentId,
            agent => agent.DisplayName = "Updated agent",
            mcpToolServerIds: null,
            skillIds: null,
            appInstanceIds: [scope.GoogleAppInstanceId],
            user: "tester");

        Assert.NotNull(updated);

        await using var assertContext = scope.CreateDbContext();
        var relations = await assertContext.Set<AgentAppRelation>()
            .Where(x => x.AgentId == scope.AgentId)
            .ToListAsync(cancellationToken);

        var relation = Assert.Single(relations);
        Assert.Equal(scope.GoogleAppInstanceId, relation.AppInstanceId);
    }

    [Fact]
    public async Task ListAgentsAsync_WhenAgentHasAppRelations_ReturnsIncludedRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken);
        var service = scope.CreateAgentAppService();

        var agents = await service.ListAgentsAsync();

        var agent = Assert.Single(agents, x => x.Id == scope.AgentId);
        Assert.Contains(agent.AgentAppRelations, x => x.AppInstanceId == scope.GithubAppInstanceId);
    }

    [Fact]
    public async Task GetAgentAsync_WhenAgentHasAppRelations_ReturnsIncludedRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken);
        var service = scope.CreateAgentAppService();

        var agent = await service.GetAgentAsync(scope.AgentId);

        Assert.NotNull(agent);
        Assert.Contains(agent!.AgentAppRelations, x => x.AppInstanceId == scope.GithubAppInstanceId);
    }

    [Fact]
    public async Task CollectNamedToolNamesAsync_WhenAgentHasRelatedApps_MergesAppDefinitionTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken);
        var service = scope.CreateAgentAppService();

        var toolNames = await service.CollectNamedToolNamesAsync(scope.AgentId, """["git_status"]""");

        Assert.Equal(
            ["github_clone", "github_list_repository", "git_status"],
            toolNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public async Task CollectNamedToolNamesAsync_WhenSameToolAppearsAcrossApps_DeduplicatesNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken, includeDuplicateGithubApp: true);
        var service = scope.CreateAgentAppService();

        var toolNames = await service.CollectNamedToolNamesAsync(scope.AgentId, """["github_clone"]""");

        Assert.Equal(["github_clone", "github_list_repository"], toolNames);
    }

    [Fact]
    public async Task CollectNamedToolNamesAsync_WhenAppDefinitionMissing_SkipsUnknownApp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await AgentRuntimeServiceTestScope.CreateAsync(cancellationToken, includeUnknownApp: true);
        var service = scope.CreateAgentAppService();

        var toolNames = await service.CollectNamedToolNamesAsync(scope.AgentId, null);

        Assert.Equal(["github_clone", "github_list_repository"], toolNames);
    }

    private sealed class AgentRuntimeServiceTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;

        private AgentRuntimeServiceTestScope(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public Guid AgentId { get; private init; }
        public Guid GithubAppInstanceId { get; private init; }
        public Guid GoogleAppInstanceId { get; private init; }

        public static async Task<AgentRuntimeServiceTestScope> CreateAsync(
            CancellationToken cancellationToken,
            bool includeDuplicateGithubApp = false,
            bool includeUnknownApp = false)
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

            var agentId = Guid.NewGuid();
            var githubAppInstanceId = Guid.NewGuid();
            var googleAppInstanceId = Guid.NewGuid();
            var duplicateGithubAppInstanceId = Guid.NewGuid();
            var unknownAppInstanceId = Guid.NewGuid();

            await using (var seedContext = new AgwDbContext(options))
            {
                seedContext.Agents.Add(new Agent
                {
                    Id = agentId,
                    Name = "existing-agent",
                    DisplayName = "Existing Agent",
                    Description = "desc",
                    SystemPrompt = "prompt",
                    Type = AgentType.External,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });
                seedContext.AppInstances.AddRange(
                    new AppInstance
                    {
                        Id = githubAppInstanceId,
                        AppName = "github",
                        ClientId = "github-client",
                        ClientSecret = "github-secret",
                        CreateBy = "tester",
                        CreateTime = DateTime.UtcNow
                    },
                    new AppInstance
                    {
                        Id = googleAppInstanceId,
                        AppName = "google-workspace",
                        ClientId = "google-client",
                        ClientSecret = "google-secret",
                        CreateBy = "tester",
                        CreateTime = DateTime.UtcNow
                    });

                if (includeDuplicateGithubApp)
                {
                    seedContext.AppInstances.Add(new AppInstance
                    {
                        Id = duplicateGithubAppInstanceId,
                        AppName = "github",
                        ClientId = "github-client-2",
                        ClientSecret = "github-secret-2",
                        CreateBy = "tester",
                        CreateTime = DateTime.UtcNow
                    });
                }

                if (includeUnknownApp)
                {
                    seedContext.AppInstances.Add(new AppInstance
                    {
                        Id = unknownAppInstanceId,
                        AppName = "missing-app",
                        ClientId = "missing-client",
                        ClientSecret = "missing-secret",
                        CreateBy = "tester",
                        CreateTime = DateTime.UtcNow
                    });
                }

                seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
                {
                    AgentId = agentId,
                    AppInstanceId = githubAppInstanceId
                });

                if (includeDuplicateGithubApp)
                {
                    seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
                    {
                        AgentId = agentId,
                        AppInstanceId = duplicateGithubAppInstanceId
                    });
                }

                if (includeUnknownApp)
                {
                    seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
                    {
                        AgentId = agentId,
                        AppInstanceId = unknownAppInstanceId
                    });
                }

                await seedContext.SaveChangesAsync(cancellationToken);
            }

            return new AgentRuntimeServiceTestScope(connection, options)
            {
                AgentId = agentId,
                GithubAppInstanceId = githubAppInstanceId,
                GoogleAppInstanceId = googleAppInstanceId
            };
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public AgentAppService CreateAgentAppService()
        {
            var dbContext = CreateDbContext();

            return new AgentAppService(
                new EfRepository<Agent>(dbContext),
                new EfRepository<AgentAppRelation>(dbContext),
                new EfRepository<AppInstance>(dbContext),
                new AppDefinitionRepo(),
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<LlmModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                new EfRepository<McpServer>(dbContext),
                new EfRepository<AgentMcpServerRelation>(dbContext),
                new EfRepository<Skill>(dbContext),
                new EfRepository<AgentSkillRelation>(dbContext),
                new UnitOfWork(dbContext),
                new AgentDomainService());
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
