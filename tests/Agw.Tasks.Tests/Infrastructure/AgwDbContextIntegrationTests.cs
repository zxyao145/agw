using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public class AgwDbContextIntegrationTests
{
    [Fact]
    public async Task ProjectRelations_WhenProjectDeletedWithoutForeignKeys_RemovesAllRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var mcpToolServerId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.Skills.Add(CreateSkill(skillId));
            seedContext.McpToolServers.Add(CreateMcpServer(mcpToolServerId));
            seedContext.AppInstances.Add(CreateAppInstance(appInstanceId, "project-delete-client"));
            seedContext.ProjectSkillRelations.Add(new ProjectSkillRelation { ProjectId = projectId, SkillId = skillId });
            seedContext.ProjectMcpToolServers.Add(new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = mcpToolServerId });
            seedContext.ProjectAppRelations.Add(new ProjectAppRelation { ProjectId = projectId, AppInstanceId = appInstanceId });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var project = await deleteContext.Projects.FindAsync([projectId], cancellationToken);
            deleteContext.Projects.Remove(project!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectAppRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task ProjectSkillRelation_WhenSkillDeletedWithoutForeignKeys_RemovesRelation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.Skills.Add(CreateSkill(skillId));
            seedContext.ProjectSkillRelations.Add(new ProjectSkillRelation { ProjectId = projectId, SkillId = skillId });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var skill = await deleteContext.Skills.FindAsync([skillId], cancellationToken);
            deleteContext.Skills.Remove(skill!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task ProjectMcpServerRelation_WhenMcpServerDeletedWithoutForeignKeys_RemovesRelation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var mcpToolServerId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.McpToolServers.Add(CreateMcpServer(mcpToolServerId));
            seedContext.ProjectMcpToolServers.Add(new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = mcpToolServerId });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var mcpToolServer = await deleteContext.McpToolServers.FindAsync([mcpToolServerId], cancellationToken);
            deleteContext.McpToolServers.Remove(mcpToolServer!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task ProjectAppRelation_WhenAppInstanceDeletedWithoutForeignKeys_RemovesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "project-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "project-client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.ProjectAppRelations.Add(new ProjectAppRelation
            {
                ProjectId = projectId,
                AppInstanceId = appInstanceId
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var appInstance = await deleteContext.AppInstances.FindAsync([appInstanceId], cancellationToken);
            deleteContext.AppInstances.Remove(appInstance!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.ProjectAppRelations.AnyAsync(cancellationToken));
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken)
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

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
        ContentPath = $"/skills/{id:N}",
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static McpServer CreateMcpServer(Guid id) => new()
    {
        Id = id,
        Name = $"mcp-{id:N}",
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static AppInstance CreateAppInstance(Guid id, string clientId) => new()
    {
        Id = id,
        AppName = "github",
        ClientId = clientId,
        ClientSecret = "secret",
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    [Fact]
    public async Task AgentAppRelation_WhenDuplicatePairInserted_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
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
        var appInstanceId = Guid.NewGuid();

        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(new Agent
            {
                Id = agentId,
                Name = "agent-1",
                DisplayName = "Agent 1",
                Description = "desc",
                SystemPrompt = "prompt",
                Type = AgentType.System,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
            {
                AgentId = agentId,
                AppInstanceId = appInstanceId
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        dbContext.Set<AgentAppRelation>().Add(new AgentAppRelation
        {
            AgentId = agentId,
            AppInstanceId = appInstanceId
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task AgentAppRelation_WhenAppInstanceDeleted_RemovesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
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
        var appInstanceId = Guid.NewGuid();

        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(new Agent
            {
                Id = agentId,
                Name = "agent-1",
                DisplayName = "Agent 1",
                Description = "desc",
                SystemPrompt = "prompt",
                Type = AgentType.System,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
            {
                AgentId = agentId,
                AppInstanceId = appInstanceId
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var appInstance = await deleteContext.AppInstances.FindAsync([appInstanceId], cancellationToken);
            deleteContext.AppInstances.Remove(appInstance!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.Set<AgentAppRelation>().AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task AppInstance_WhenAppNameDuplicatesAndClientIdDiffers_AllowsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            dbContext.AppInstances.AddRange(
                new AppInstance
                {
                    Id = Guid.NewGuid(),
                    AppName = "github",
                    ClientId = "client-1",
                    ClientSecret = "secret-1",
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow()
                },
                new AppInstance
                {
                    Id = Guid.NewGuid(),
                    AppName = "github",
                    ClientId = "client-2",
                    ClientSecret = "secret-2",
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow()
                });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        var instances = await assertContext.AppInstances
            .Where(instance => instance.AppName == "github")
            .ToListAsync(cancellationToken);

        Assert.Equal(2, instances.Count);
    }

    [Fact]
    public async Task AppInstance_WhenClientIdDuplicates_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var sharedClientId = "shared-client";
        await using var dbContext = new AgwDbContext(options);
        dbContext.AppInstances.AddRange(
            new AppInstance
            {
                Id = Guid.NewGuid(),
                AppName = "github",
                ClientId = sharedClientId,
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            },
            new AppInstance
            {
                Id = Guid.NewGuid(),
                AppName = "google-workspace",
                ClientId = sharedClientId,
                ClientSecret = "secret-2",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task OAuthAuthorizationToken_WhenSecondTokenUsesSameAppInstance_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var appInstanceId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });

            seedContext.OAuthAuthorizationTokens.Add(new OAuthAuthorizationToken
            {
                Id = Guid.NewGuid(),
                AppInstanceId = appInstanceId,
                Subject = "subject-1",
                AccessToken = "access-token-1"
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        dbContext.OAuthAuthorizationTokens.Add(new OAuthAuthorizationToken
        {
            Id = Guid.NewGuid(),
            AppInstanceId = appInstanceId,
            Subject = "subject-2",
            AccessToken = "access-token-2"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }
}
