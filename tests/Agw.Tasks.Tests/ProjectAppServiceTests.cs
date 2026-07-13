using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectAppServiceTests
{
    [Fact]
    public async Task CreateAsync_RelationIdsContainInvalidValues_PersistsOnlyDistinctExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);

        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId, scope.FirstMcpToolServerId, Guid.Empty, Guid.NewGuid()],
            [scope.FirstSkillId, scope.FirstSkillId, Guid.Empty, Guid.NewGuid()],
            [scope.FirstAppInstanceId, scope.FirstAppInstanceId, Guid.Empty, Guid.NewGuid()],
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
            [scope.FirstAppInstanceId],
            await assertContext.ProjectAppRelations.Select(relation => relation.AppInstanceId).ToArrayAsync(cancellationToken));
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
            [scope.FirstAppInstanceId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.SecondMcpToolServerId],
            [scope.SecondSkillId],
            [scope.SecondAppInstanceId],
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
            [scope.SecondAppInstanceId],
            await assertContext.ProjectAppRelations.Select(relation => relation.AppInstanceId).ToArrayAsync(cancellationToken));
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
            [scope.FirstAppInstanceId],
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
        Assert.Equal(scope.FirstAppInstanceId, await assertContext.ProjectAppRelations.Select(relation => relation.AppInstanceId).SingleAsync(cancellationToken));
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
            [scope.FirstAppInstanceId],
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
        Assert.False(await assertContext.ProjectAppRelations.AnyAsync(cancellationToken));
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
            [scope.FirstAppInstanceId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            "updater");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(scope.FirstMcpToolServerId, await assertContext.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstSkillId, await assertContext.ProjectSkillRelations.Select(relation => relation.SkillId).SingleAsync(cancellationToken));
        Assert.Equal(scope.FirstAppInstanceId, await assertContext.ProjectAppRelations.Select(relation => relation.AppInstanceId).SingleAsync(cancellationToken));
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
            [scope.FirstAppInstanceId],
            "tester");

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstAppInstanceId],
            "updater");

        Assert.NotNull(updated);
        AssertProjectRelations(updated, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstAppInstanceId);
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
            [scope.FirstAppInstanceId],
            "tester");

        var listed = Assert.Single(await scope.Service.ListAsync());
        var fetched = await scope.Service.GetAsync(created!.Id);

        AssertProjectRelations(listed, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstAppInstanceId);
        Assert.NotNull(fetched);
        AssertProjectRelations(fetched, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstAppInstanceId);
    }

    [Fact]
    public async Task DeleteAsync_WhenProjectHasRelations_CascadeDeletesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var created = await scope.Service.CreateAsync(
            CreateProject("Project A"),
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstAppInstanceId],
            "tester");

        var deleted = await scope.Service.DeleteAsync(created!.Id);

        Assert.True(deleted);
        await using var assertContext = scope.CreateDbContext();
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectAppRelations.AnyAsync(cancellationToken));
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
            new EfRepository<ProjectAppRelation>(dbContext),
            new EfRepository<AppInstance>(dbContext),
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
        Guid appInstanceId)
    {
        Assert.Equal(mcpToolServerId, Assert.Single(project.ProjectMcpToolServers).McpToolServerId);
        Assert.Equal(skillId, Assert.Single(project.ProjectSkillRelations).SkillId);
        Assert.Equal(appInstanceId, Assert.Single(project.ProjectAppRelations).AppInstanceId);
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
        public Guid FirstAppInstanceId { get; } = Guid.NewGuid();
        public Guid SecondAppInstanceId { get; } = Guid.NewGuid();

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
            dbContext.AppInstances.AddRange(
                new AppInstance
                {
                    Id = scope.FirstAppInstanceId,
                    AppName = "github",
                    ClientId = "client-1",
                    ClientSecret = "secret-1"
                },
                new AppInstance
                {
                    Id = scope.SecondAppInstanceId,
                    AppName = "github",
                    ClientId = "client-2",
                    ClientSecret = "secret-2"
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
