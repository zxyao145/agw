using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Controllers;
using Agw.Agents.Definitions.Facades;
using Agw.Domain.Services.Skills;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Remote;
using Agw.Skills.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class DefinitionPaginationTests
{
    [Theory]
    [InlineData(typeof(AgentsController))]
    [InlineData(typeof(AgentflowsController))]
    [InlineData(typeof(McpToolServersController))]
    [InlineData(typeof(SkillsController))]
    public void Controller_ExposesPagedListWithExpectedDefaults(Type controllerType)
    {
        var method = controllerType
            .GetMethods()
            .SingleOrDefault(candidate =>
                candidate
                    .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                    .Cast<HttpGetAttribute>()
                    .Any(attribute => attribute.Template == "paged")
            );

        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Equal("pageIndex", parameters[0].Name);
        Assert.Equal(1, parameters[0].DefaultValue);
        Assert.Equal("pageSize", parameters[1].Name);
        Assert.Equal(20, parameters[1].DefaultValue);
    }

    [Fact]
    public async Task ListAgentPageAsync_IncludesConfiguredRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var now = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "paged-agent",
            DisplayName = "Paged Agent",
            Type = AgentType.External,
            CreateBy = "tester",
            CreateTime = now,
        };
        var mcpServer = new McpServer
        {
            Id = Guid.CreateVersion7(),
            Name = "paged-mcp",
            TransportType = "stdio",
            CreateBy = "tester",
            CreateTime = now,
        };
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "paged-skill",
            Description = "Paged skill",
            ContentPath = "skills/paged-skill",
            Kind = SkillKind.Local,
            CreateBy = "tester",
            CreateTime = now,
        };
        var connection = new Connection
        {
            Id = Guid.CreateVersion7(),
            PluginId = "test-plugin",
            ConnectorId = "test-connector",
            AuthSchemeId = "test-auth",
            DisplayName = "Paged Connection",
            Alias = "paged-connection",
            CreateBy = "tester",
            CreateTime = now,
        };

        database.Context.AddRange(agent, mcpServer, skill, connection);
        database.Context.AddRange(
            new AgentMcpServerRelation { AgentId = agent.Id, McpToolServerId = mcpServer.Id },
            new AgentSkillRelation { AgentId = agent.Id, SkillId = skill.Id },
            new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = connection.Id }
        );
        await database.Context.SaveChangesAsync(cancellationToken);

        var service = CreateAgentAppService(database.Context);
        var result = await service.ListAgentPageAsync(1, 10, cancellationToken);
        var listed = Assert.Single(result.Items);

        Assert.Single(listed.AgentMcpToolServers);
        Assert.Single(listed.AgentSkillRelations);
        Assert.Single(listed.AgentConnectionRelations);
    }

    [Fact]
    public async Task ListSkillPageAsync_IncludesAgentIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var now = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "skill-agent",
            DisplayName = "Skill Agent",
            Type = AgentType.External,
            CreateBy = "tester",
            CreateTime = now,
        };
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "related-skill",
            Description = "Related skill",
            ContentPath = "skills/related-skill",
            Kind = SkillKind.Local,
            CreateBy = "tester",
            CreateTime = now,
        };

        database.Context.AddRange(agent, skill);
        database.Context.AgentSkillRelations.Add(new AgentSkillRelation { AgentId = agent.Id, SkillId = skill.Id });
        await database.Context.SaveChangesAsync(cancellationToken);

        var service = new SkillAppService(
            new EfRepository<Skill>(database.Context),
            new AgentCatalogFacade(
                new EfRepository<Agent>(database.Context),
                new EfRepository<Agentflow>(database.Context),
                new EfRepository<McpServer>(database.Context),
                new EfRepository<AgentSkillRelation>(database.Context),
                database.Context,
                new TestUserInfoService()
            ),
            new EfRepository<RemoteSkillCache>(database.Context),
            database.Context,
            new SkillDomainService(TimeProvider.System),
            AgwDataPaths.Resolve(Path.Combine(Path.GetTempPath(), "agw-pagination-tests"), Path.GetTempPath()),
            NullLogger<SkillAppService>.Instance,
            new TestRemoteSkillClient(),
            new TestRemoteSkillRefreshLock(),
            TimeProvider.System,
            new TestUserInfoService()
        );

        var result = await service.ListPageAsync(1, 10, cancellationToken);
        var listed = Assert.Single(result.Items);

        Assert.Equal(new[] { agent.Id }, listed.AgentIds);
    }

    private static AgentAppService CreateAgentAppService(AgwDbContext dbContext)
    {
        return new AgentAppService(
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
            new AgentDomainService(TimeProvider.System),
            new TestUserInfoService()
        );
    }

    private sealed class TestRemoteSkillClient : IRemoteSkillClient
    {
        public string NormalizeUrl(string? remoteUrl) => throw new NotSupportedException();

        public Task<RemoteSkillDefinition> FetchAsync(
            string remoteUrl,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class TestRemoteSkillRefreshLock : IRemoteSkillRefreshLock
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, AgwDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public AgwDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
            var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
