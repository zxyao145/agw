using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public class AgwDbContextIntegrationTests
{
    [Fact]
    public async Task Project_WhenDeletedWithoutForeignKeys_RemovesCapabilityRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(foreignKeys: false, cancellationToken);
        var projectId = Guid.CreateVersion7();
        var skillId = Guid.CreateVersion7();
        var mcpId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        await using (var seed = scope.CreateContext())
        {
            seed.Projects.Add(CreateProject(projectId));
            seed.Skills.Add(CreateSkill(skillId));
            seed.McpToolServers.Add(CreateMcpServer(mcpId));
            seed.Connections.Add(CreateConnection(connectionId, "project-connection"));
            seed.ProjectSkillRelations.Add(new ProjectSkillRelation { ProjectId = projectId, SkillId = skillId });
            seed.ProjectMcpToolServers.Add(new ProjectMcpServerRelation
            {
                ProjectId = projectId,
                McpToolServerId = mcpId
            });
            seed.ProjectConnectionRelations.Add(new ProjectConnectionRelation
            {
                ProjectId = projectId,
                ConnectionId = connectionId
            });
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var delete = scope.CreateContext())
        {
            delete.Projects.Remove((await delete.Projects.FindAsync([projectId], cancellationToken))!);
            await delete.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = scope.CreateContext();
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task Connection_WhenDeletedWithoutForeignKeys_RemovesCredentialsAndBindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(foreignKeys: false, cancellationToken);
        var agentId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        await using (var seed = scope.CreateContext())
        {
            seed.Agents.Add(CreateAgent(agentId));
            seed.Projects.Add(CreateProject(projectId));
            seed.Connections.Add(CreateConnection(connectionId, "shared-connection"));
            seed.ConnectionCredentials.Add(new ConnectionCredential
            {
                Id = Guid.CreateVersion7(),
                ConnectionId = connectionId,
                Slot = "oauth.access-token",
                Value = "plaintext"
            });
            seed.AgentConnectionRelations.Add(new AgentConnectionRelation
            {
                AgentId = agentId,
                ConnectionId = connectionId
            });
            seed.ProjectConnectionRelations.Add(new ProjectConnectionRelation
            {
                ProjectId = projectId,
                ConnectionId = connectionId
            });
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var delete = scope.CreateContext())
        {
            delete.Connections.Remove((await delete.Connections.FindAsync([connectionId], cancellationToken))!);
            await delete.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = scope.CreateContext();
        Assert.False(await assertContext.ConnectionCredentials.AnyAsync(cancellationToken));
        Assert.False(await assertContext.AgentConnectionRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task AgentConnectionRelation_WhenDuplicatePairInserted_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(foreignKeys: true, cancellationToken);
        var agentId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        await using (var seed = scope.CreateContext())
        {
            seed.Agents.Add(CreateAgent(agentId));
            seed.Connections.Add(CreateConnection(connectionId, "agent-connection"));
            seed.AgentConnectionRelations.Add(new AgentConnectionRelation
            {
                AgentId = agentId,
                ConnectionId = connectionId
            });
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var duplicate = scope.CreateContext();
        duplicate.AgentConnectionRelations.Add(new AgentConnectionRelation
        {
            AgentId = agentId,
            ConnectionId = connectionId
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(cancellationToken));
    }

    private static Agent CreateAgent(Guid id) => new()
    {
        Id = id,
        Name = $"agent-{id:N}",
        DisplayName = "Agent",
        Description = "desc",
        SystemPrompt = "prompt",
        Type = AgentType.External,
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static Project CreateProject(Guid id) => new()
    {
        Id = id,
        Name = $"project-{id:N}",
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static Skill CreateSkill(Guid id) => new()
    {
        Id = id,
        Name = $"skill-{id:N}",
        Description = "Skill",
        ContentPath = $"/skills/{id:N}"
    };

    private static McpServer CreateMcpServer(Guid id) => new()
    {
        Id = id,
        Name = $"mcp-{id:N}"
    };

    private static Connection CreateConnection(Guid id, string alias) => new()
    {
        Id = id,
        PluginId = "github",
        ConnectorId = "github-cloud",
        AuthSchemeId = "oauth",
        DisplayName = alias,
        Alias = alias
    };

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;

        private TestScope(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<TestScope> CreateAsync(bool foreignKeys, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(
                $"Data Source=:memory:;Foreign Keys={(foreignKeys ? "True" : "False")}");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var setup = new AgwDbContext(options);
            await setup.Database.EnsureCreatedAsync(cancellationToken);
            return new TestScope(connection, options);
        }

        public AgwDbContext CreateContext() => new(_options);

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
