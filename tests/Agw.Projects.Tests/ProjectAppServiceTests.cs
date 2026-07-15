using Agw.Files.Abstracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProjectAppServiceTests
{
    [Fact]
    public async Task ProjectFileSystemConfigurationProvider_WhenCanceled_StopsBeforeLookup()
    {
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(
            TestContext.Current.CancellationToken);
        IProjectFileSystemConfigurationProvider provider =
            new ProjectFileSystemConfigurationProvider(scope.Service);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetAsync(Guid.NewGuid(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ProjectFileSystemConfigurationProvider_WhenProjectExists_ReturnsFileConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
            var project = CreateProject("Project A");
            project.Workspace = workspace;
            project.ExtraSetting = "{\"fileStorage\":{\"type\":\"local\"}}";
            var created = await scope.Service.CreateAsync(project, "tester");
            IProjectFileSystemConfigurationProvider provider =
                new ProjectFileSystemConfigurationProvider(scope.Service);
            var configuration = await provider.GetAsync(created!.Id, cancellationToken);

            Assert.NotNull(configuration);
            Assert.Equal(project.Name, configuration.Name);
            Assert.Equal(project.Workspace, configuration.Workspace);
            Assert.Equal(project.ExtraSetting, configuration.ExtraSetting);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_RelationIdsContainInvalidValues_PersistsOnlyDistinctExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);

        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId, scope.FirstMcpToolServerId, Guid.Empty, Guid.NewGuid()],
            [scope.FirstSkillId, scope.FirstSkillId, Guid.Empty, Guid.NewGuid()],
            [scope.FirstConnectionId, scope.FirstConnectionId, Guid.Empty, Guid.NewGuid()],
            "tester");

        Assert.NotNull(created);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(
            [scope.FirstMcpToolServerId],
            await assertContext.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).ToArrayAsync(cancellationToken));
        Assert.Equal(
            [scope.FirstSkillId],
            await assertContext.ProjectSkillRelations.Select(relation => relation.SkillId).ToArrayAsync(cancellationToken));
        Assert.Equal(
            [scope.FirstConnectionId],
            await assertContext.ProjectConnectionRelations.Select(relation => relation.ConnectionId).ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenRelationIdsChange_ReplacesExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.SecondMcpToolServerId],
            [scope.SecondSkillId],
            [scope.SecondConnectionId],
            "updater");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(
            [scope.SecondMcpToolServerId],
            await assertContext.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).ToArrayAsync(cancellationToken));
        Assert.Equal(
            [scope.SecondSkillId],
            await assertContext.ProjectSkillRelations.Select(relation => relation.SkillId).ToArrayAsync(cancellationToken));
        Assert.Equal(
            [scope.SecondConnectionId],
            await assertContext.ProjectConnectionRelations.Select(relation => relation.ConnectionId).ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenRelationIdsAreNull_PreservesExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            null,
            null,
            null,
            "updater");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(scope.FirstMcpToolServerId, await assertContext.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstSkillId, await assertContext.ProjectSkillRelations.Select(relation => relation.SkillId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstConnectionId, await assertContext.ProjectConnectionRelations.Select(relation => relation.ConnectionId).SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenRelationIdsAreExplicitlyEmpty_ClearsExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [],
            [],
            [],
            "updater");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task LegacyUpdateAsync_WhenProjectHasRelations_PreservesExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            "updater");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(scope.FirstMcpToolServerId, await assertContext.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstSkillId, await assertContext.ProjectSkillRelations.Select(relation => relation.SkillId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstConnectionId, await assertContext.ProjectConnectionRelations.Select(relation => relation.ConnectionId).SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenRelationIdsAreUnchanged_KeepsExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "updater");

        Assert.NotNull(updated);
        AssertProjectRelations(updated, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstConnectionId);
    }

    [Fact]
    public async Task ListAndGetAsync_WhenProjectHasRelations_ReturnEagerLoadedRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");

        var listed = Assert.Single(await scope.Service.ListAsync());
        var fetched = await scope.Service.GetAsync(created!.Id);

        AssertProjectRelations(listed, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstConnectionId);
        Assert.NotNull(fetched);
        AssertProjectRelations(fetched, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstConnectionId);
    }

    [Fact]
    public async Task DeleteAsync_WhenProjectHasRelations_CascadeDeletesRelationsAndPreservesUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId],
            "tester");
        await using (var usageContext = scope.CreateDbContext())
        {
            usageContext.AgentUsages.Add(new AgentUsage
            {
                Id = Guid.NewGuid(),
                ProjectId = created!.Id,
                ContextId = "context-1",
                AgentName = "planner",
                RecordedAt = TimeProvider.System.GetUtcNow(),
                TotalTokenCount = 10
            });
            await usageContext.SaveChangesAsync(cancellationToken);
        }

        var deleted = await scope.Service.DeleteAsync(created.Id);

        Assert.True(deleted);
        await using var assertContext = scope.CreateDbContext();
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
        Assert.Equal(created.Id, Assert.Single(await assertContext.AgentUsages.ToListAsync(cancellationToken)).ProjectId);
    }

    [Fact]
    public async Task CreateAsync_WhenWorkspaceDoesNotExist_CreatesWorkspaceDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");

        try
        {
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

            await using var dbContext = new AgwDbContext(options);
            var service = CreateService(dbContext);

            var created = await service.CreateAsync(
                new Project
                {
                    Name = "Project A",
                    Type = ProjectType.UserDefined,
                    Workspace = workspace,
                    Enable = true
                },
                "tester");

            Assert.NotNull(created);
            Assert.True(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static ProjectAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectAppService(
            projectRepository,
            new EfRepository<ProjectMcpServerRelation>(dbContext),
            new EfRepository<McpServer>(dbContext),
            new EfRepository<ProjectSkillRelation>(dbContext),
            new EfRepository<Skill>(dbContext),
            new EfRepository<ProjectConnectionRelation>(dbContext),
            new EfRepository<Connection>(dbContext),
            new EfRepository<AgentflowTrace>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectDomainService(TimeProvider.System),
            new ProjectResolver(projectRepository));
    }

    private static Project CreateProject(string name) => new()
    {
        Name = name,
        Type = ProjectType.UserDefined,
        Workspace = Path.GetTempPath(),
        Enable = true
    };

    private static void AssertProjectRelations(
        Project project,
        Guid mcpToolServerId,
        Guid skillId,
        Guid connectionId)
    {
        Assert.Equal(mcpToolServerId, Assert.Single(project.ProjectMcpToolServers).McpToolServerId);
        Assert.Equal(skillId, Assert.Single(project.ProjectSkillRelations).SkillId);
        Assert.Equal(connectionId, Assert.Single(project.ProjectConnectionRelations).ConnectionId);
    }

    private sealed class ProjectAppServiceTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _dbContext;

        private ProjectAppServiceTestScope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            AgwDbContext dbContext)
        {
            _connection = connection;
            _options = options;
            _dbContext = dbContext;
            Service = CreateService(dbContext);
        }

        public ProjectAppService Service { get; }
        public Guid FirstMcpToolServerId { get; } = Guid.NewGuid();
        public Guid SecondMcpToolServerId { get; } = Guid.NewGuid();
        public Guid FirstSkillId { get; } = Guid.NewGuid();
        public Guid SecondSkillId { get; } = Guid.NewGuid();
        public Guid FirstConnectionId { get; } = Guid.NewGuid();
        public Guid SecondConnectionId { get; } = Guid.NewGuid();

        public static async Task<ProjectAppServiceTestScope> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            var scope = new ProjectAppServiceTestScope(connection, options, dbContext);
            dbContext.McpToolServers.AddRange(
                new McpServer { Id = scope.FirstMcpToolServerId, Name = "MCP 1" },
                new McpServer { Id = scope.SecondMcpToolServerId, Name = "MCP 2" });
            dbContext.Skills.AddRange(
                new Skill { Id = scope.FirstSkillId, Name = "skill-1", Description = "Skill 1", ContentPath = "/skills/1" },
                new Skill { Id = scope.SecondSkillId, Name = "skill-2", Description = "Skill 2", ContentPath = "/skills/2" });
            dbContext.Connections.AddRange(
                new Connection
                {
                    Id = scope.FirstConnectionId,
                    PluginId = "github",
                    ConnectorId = "github-cloud",
                    AuthSchemeId = "oauth",
                    DisplayName = "Work GitHub",
                    Alias = "work-github"
                },
                new Connection
                {
                    Id = scope.SecondConnectionId,
                    PluginId = "github",
                    ConnectorId = "github-cloud",
                    AuthSchemeId = "oauth",
                    DisplayName = "Personal GitHub",
                    Alias = "personal-github"
                });
            await dbContext.SaveChangesAsync(cancellationToken);
            return scope;
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
