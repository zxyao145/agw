using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Controllers;
using Agw.Agents.Definitions.Facades;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Remote;
using Agw.Skills.Controllers;
using Agw.Testing;
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
    public async Task AgentLists_ExcludeDisabledFromSelectableListAndKeepThemInPagedManagementList()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var now = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var enabledAgent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "enabled-agent",
            DisplayName = "Enabled Agent",
            Type = AgentType.External,
            Enable = true,
            CreateBy = "tester",
            CreateTime = now,
        };
        var disabledAgent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "disabled-agent",
            DisplayName = "Disabled Agent",
            Type = AgentType.External,
            Enable = false,
            CreateBy = "tester",
            CreateTime = now,
        };
        database.Context.Agents.AddRange(enabledAgent, disabledAgent);
        await database.Context.SaveChangesAsync(cancellationToken);
        var service = CreateAgentAppService(database.Context);

        // Act
        var selectable = await service.ListAgentsForCurrentUserAsync();
        var managed = await service.ListAgentPageForCurrentUserAsync(1, 10, cancellationToken);

        // Assert
        Assert.Equal(enabledAgent.Id, Assert.Single(selectable).Id);
        Assert.Equal(2, managed.Total);
    }

    [Fact]
    public async Task UpdateAgentEnabledAsync_OwnedAgent_PersistsEnabledStateAndRefreshesAuditMetadata()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var originalUpdateTime = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var expectedUpdateTime = originalUpdateTime.AddMinutes(5);
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityModifierInterceptor(
                    new TestAuditUserIdProvider("tester"),
                    new TestTimeProvider(expectedUpdateTime)
                )
            )
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "disabled-agent",
            DisplayName = "Disabled Agent",
            Type = AgentType.External,
            Enable = false,
            CreateBy = "tester",
            CreateTime = originalUpdateTime.AddDays(-1),
            UpdateBy = "original-user",
            UpdateTime = originalUpdateTime,
        };
        dbContext.Agents.Add(agent);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        var service = CreateAgentAppService(dbContext);

        // Act
        var updated = await service.UpdateAgentEnabledAsync(agent.Id, true, cancellationToken);

        // Assert
        Assert.NotNull(updated);
        Assert.True(updated.Enable);
        Assert.Equal("tester", updated.UpdateBy);
        Assert.Equal(expectedUpdateTime, updated.UpdateTime);
        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Agents.AsNoTracking().SingleAsync(cancellationToken);
        Assert.True(persisted.Enable);
        Assert.Equal("tester", persisted.UpdateBy);
        Assert.Equal(expectedUpdateTime, persisted.UpdateTime);
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

    [Fact]
    public async Task CreateAndUpdateAgentAsync_ExternalAgent_PersistsOwnerAndRefreshesAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var createdAt = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(createdAt);
        var auditUser = new TestAuditUserIdProvider("tester");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUser, clock),
                new EntityModifierInterceptor(auditUser, clock)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var service = CreateAgentAppService(context);

        // Act
        var agent = await service.CreateAgentAsync(
            new Agent
            {
                Type = AgentType.External,
                Name = "",
                DisplayName = "before",
            },
            [],
            [],
            []
        );

        // Assert
        Assert.NotNull(agent);
        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal(agent.Id.ToString(), agent.Name);
        Assert.Equal("tester", agent.CreateBy);
        Assert.Equal(createdAt, agent.CreateTime);

        for (var update = 1; update <= 3; update++)
        {
            context.ChangeTracker.Clear();
            var updatedAt = createdAt.AddMinutes(update);
            clock.SetUtcNow(updatedAt);
            // The third save changes no domain fields and must still refresh the root audit.
            var valueVersion = Math.Min(update, 2);
            var command = new AgentUpdateCommand(
                $"name-{valueVersion}",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "  {\"enabled\":true}  ",
                new Dictionary<string, string> { [" KEY "] = $"{valueVersion}" },
                null,
                null,
                [AgentUpdateField.DisplayName, AgentUpdateField.Extra, AgentUpdateField.EnvironmentVariables]
            );

            var updated = await service.UpdateAgentAsync(agent.Id, command);
            Assert.NotNull(updated);
            context.ChangeTracker.Clear();
            var persisted = await context.Agents.SingleAsync(cancellationToken);

            Assert.Equal($"name-{valueVersion}", persisted.DisplayName);
            Assert.Equal(agent.Name, persisted.Name);
            Assert.Equal("{\"enabled\":true}", persisted.Extra);
            Assert.Equal($"{valueVersion}", persisted.EnvironmentVariables["KEY"]);
            Assert.Equal("tester", persisted.CreateBy);
            Assert.Equal(createdAt, persisted.CreateTime);
            Assert.Equal("tester", persisted.UpdateBy);
            Assert.Equal(updatedAt, persisted.UpdateTime);
        }
    }

    [Fact]
    public async Task UpdateAgentAsync_TrackedBindings_ReconcilesKeysAndRefreshesAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var createdAt = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(createdAt);
        var auditUser = new TestAuditUserIdProvider("tester");
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUser, clock),
                new EntityModifierInterceptor(auditUser, clock)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var service = CreateAgentAppService(context);
        var servers = new[]
        {
            new McpServer { Name = "first" },
            new McpServer { Name = "second" },
        };
        var skills = new[]
        {
            new Skill { Name = "first" },
            new Skill { Name = "second" },
        };
        var connections = new[]
        {
            new Connection { Alias = "first" },
            new Connection { Alias = "second" },
        };
        var model = new AgwAiModel { Id = Guid.CreateVersion7(), Name = "model" };
        var provider = new Provider { Id = Guid.CreateVersion7(), Name = "provider" };
        var modelProvider = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = model.Id,
            ProviderId = provider.Id,
        };
        context.AddRange(model, provider, modelProvider);
        context.McpToolServers.AddRange(servers);
        context.Skills.AddRange(skills);
        context.Connections.AddRange(connections);
        await context.SaveChangesAsync(cancellationToken);
        var agent = await service.CreateAgentAsync(
            new Agent
            {
                Name = "system",
                DisplayName = "System",
                Type = AgentType.System,
                ModelProviderId = modelProvider.Id,
            },
            [servers[0].Id],
            [skills[0].Id],
            [connections[0].Id]
        );
        Assert.NotNull(agent);
        var originalMcp = await context.AgentMcpToolServers.SingleAsync(cancellationToken);
        var originalSkill = await context.AgentSkillRelations.SingleAsync(cancellationToken);
        var originalConnection = await context.AgentConnectionRelations.SingleAsync(cancellationToken);

        for (var update = 1; update <= 3; update++)
        {
            // Act: retain + add, remove the old keys, then repeat without any field changes.
            clock.SetUtcNow(createdAt.AddMinutes(update));
            var keepBoth = update == 1;
            var command = new AgentUpdateCommand(
                "System",
                "",
                "",
                modelProvider.Id,
                null,
                keepBoth ? [servers[0].Id, servers[1].Id] : [servers[1].Id],
                keepBoth ? [skills[0].Id, skills[1].Id] : [skills[1].Id],
                keepBoth ? [connections[0].Id, connections[1].Id] : [connections[1].Id],
                null,
                null,
                false,
                null,
                [
                    AgentUpdateField.DisplayName,
                    AgentUpdateField.Description,
                    AgentUpdateField.SystemPrompt,
                    AgentUpdateField.ModelProviderId,
                    AgentUpdateField.ConnectionIds,
                ]
            );
            Assert.NotNull(await service.UpdateAgentAsync(agent.Id, command));

            // Assert
            var mcps = await context.AgentMcpToolServers.ToListAsync(cancellationToken);
            var skillLinks = await context.AgentSkillRelations.ToListAsync(cancellationToken);
            var connectionLinks = await context.AgentConnectionRelations.ToListAsync(cancellationToken);
            Assert.Equal(keepBoth ? 2 : 1, mcps.Count);
            Assert.Equal(keepBoth ? 2 : 1, skillLinks.Count);
            Assert.Equal(keepBoth ? 2 : 1, connectionLinks.Count);
            Assert.Contains(mcps, link => link.McpToolServerId == servers[1].Id);
            Assert.Contains(skillLinks, link => link.SkillId == skills[1].Id);
            Assert.Contains(connectionLinks, link => link.ConnectionId == connections[1].Id);
            if (keepBoth)
            {
                Assert.Same(originalMcp, mcps.Single(link => link.McpToolServerId == servers[0].Id));
                Assert.Same(originalSkill, skillLinks.Single(link => link.SkillId == skills[0].Id));
                Assert.Same(originalConnection, connectionLinks.Single(link => link.ConnectionId == connections[0].Id));
            }
            var persisted = await context.Agents.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal("tester", persisted.CreateBy);
            Assert.Equal(createdAt, persisted.CreateTime);
            Assert.Equal("tester", persisted.UpdateBy);
            Assert.Equal(clock.GetUtcNow(), persisted.UpdateTime);
        }
    }

    private static AgentAppService CreateAgentAppService(AgwDbContext dbContext)
    {
        var connectionRepository = new EfRepository<Connection>(dbContext);
        var modelProviderRepository = new EfRepository<ModelProviderRelation>(dbContext);
        var modelRepository = new EfRepository<AgwAiModel>(dbContext);
        var providerRepository = new EfRepository<Provider>(dbContext);
        var skillRepository = new EfRepository<Skill>(dbContext);
        var userInfo = new TestUserInfoService();
        return new AgentAppService(
            dbContext,
            new TestConnectionReferenceFacade(connectionRepository, userInfo),
            new TestModelProviderReferenceFacade(
                modelProviderRepository,
                modelRepository,
                providerRepository,
                userInfo
            ),
            new TestSkillReferenceFacade(skillRepository, userInfo),
            userInfo
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

    private sealed class TestAuditUserIdProvider : IEntityAuditUserIdProvider
    {
        private readonly string _userId;

        public TestAuditUserIdProvider(string userId)
        {
            _userId = userId;
        }

        public string GetUserId() => _userId;
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
