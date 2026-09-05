using System.Security.Claims;
using Agw.Files.Abstracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Facades;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Facades;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProjectAppServiceTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task ProjectFileSystemConfigurationProvider_WhenCanceled_StopsBeforeLookup()
    {
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(TestContext.Current.CancellationToken);
        IProjectFileSystemConfigurationProvider provider = new ProjectFileSystemConfigurationProvider(scope.Service);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetAsync(Guid.CreateVersion7(), new CancellationToken(canceled: true))
        );
    }

    [Fact]
    public async Task ProjectFileSystemConfigurationProvider_WhenProjectExists_ReturnsWorkspaceOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.CreateVersion7().ToString("N"));
        try
        {
            await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
            var project = CreateProject("Project A");
            project.Workspace = workspace;
            project.ExtraSetting = "{\"fileStorage\":{\"type\":\"local\"}}";
            var created = await scope.Service.CreateAsync(project);
            IProjectFileSystemConfigurationProvider provider = new ProjectFileSystemConfigurationProvider(
                scope.Service
            );
            var configuration = await provider.GetAsync(created!.Id, cancellationToken);

            Assert.NotNull(configuration);
            Assert.Equal(project.Name, configuration.Name);
            Assert.Equal(project.Workspace, configuration.Workspace);
            Assert.Null(typeof(ProjectFileSystemConfiguration).GetProperty("ExtraSetting"));
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
    public async Task UpdateAsync_WhenWorkspaceChanges_InvalidatesFileSystemCache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var cache = new RecordingFileSystemCacheInvalidator();
        var service = CreateService(dbContext, fileSystemCache: cache);
        var project = await service.CreateAsync(CreateProject("Project A"));
        var workspace = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.CreateVersion7().ToString("N"));

        try
        {
            var updated = await service.UpdateAsync(project!.Id, item => item.Workspace = workspace);

            Assert.NotNull(updated);
            Assert.Equal([project.Id], cache.InvalidatedProjectIds);
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
    public async Task CreateAsync_RelationIdsContainInvalidValues_ThrowsInvalidParam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Service.CreateAsync(
                CreateProject("Project A"),
                [scope.FirstMcpToolServerId, scope.FirstMcpToolServerId, Guid.Empty, Guid.CreateVersion7()],
                [scope.FirstSkillId, scope.FirstSkillId, Guid.Empty, Guid.CreateVersion7()],
                [scope.FirstConnectionId, scope.FirstConnectionId, Guid.Empty, Guid.CreateVersion7()]
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
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
            [scope.FirstConnectionId]
        );

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.SecondMcpToolServerId],
            [scope.SecondSkillId],
            [scope.SecondConnectionId]
        );

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(
            [scope.SecondMcpToolServerId],
            await assertContext
                .ProjectMcpToolServers.Select(relation => relation.McpToolServerId)
                .ToArrayAsync(cancellationToken)
        );
        Assert.Equal(
            [scope.SecondSkillId],
            await assertContext
                .ProjectSkillRelations.Select(relation => relation.SkillId)
                .ToArrayAsync(cancellationToken)
        );
        Assert.Equal(
            [scope.SecondConnectionId],
            await assertContext
                .ProjectConnectionRelations.Select(relation => relation.ConnectionId)
                .ToArrayAsync(cancellationToken)
        );
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
            [scope.FirstConnectionId]
        );

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            null,
            null,
            null
        );

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(
            scope.FirstMcpToolServerId,
            await assertContext
                .ProjectMcpToolServers.Select(relation => relation.McpToolServerId)
                .SingleAsync(cancellationToken)
        );
        Assert.Equal(
            scope.FirstSkillId,
            await assertContext
                .ProjectSkillRelations.Select(relation => relation.SkillId)
                .SingleAsync(cancellationToken)
        );
        Assert.Equal(
            scope.FirstConnectionId,
            await assertContext
                .ProjectConnectionRelations.Select(relation => relation.ConnectionId)
                .SingleAsync(cancellationToken)
        );
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
            [scope.FirstConnectionId]
        );

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [],
            [],
            []
        );

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
            [scope.FirstConnectionId]
        );

        var updated = await scope.Service.UpdateAsync(created!.Id, project => project.Description = "Updated");

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        Assert.Equal(
            scope.FirstMcpToolServerId,
            await assertContext
                .ProjectMcpToolServers.Select(relation => relation.McpToolServerId)
                .SingleAsync(cancellationToken)
        );
        Assert.Equal(
            scope.FirstSkillId,
            await assertContext
                .ProjectSkillRelations.Select(relation => relation.SkillId)
                .SingleAsync(cancellationToken)
        );
        Assert.Equal(
            scope.FirstConnectionId,
            await assertContext
                .ProjectConnectionRelations.Select(relation => relation.ConnectionId)
                .SingleAsync(cancellationToken)
        );
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
            [scope.FirstConnectionId]
        );

        var updated = await scope.Service.UpdateAsync(
            created!.Id,
            project => project.Description = "Updated",
            [scope.FirstMcpToolServerId],
            [scope.FirstSkillId],
            [scope.FirstConnectionId]
        );

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
            [scope.FirstConnectionId]
        );

        var listed = Assert.Single(await scope.Service.ListAsync());
        var fetched = await scope.Service.GetAsync(created!.Id);

        AssertProjectRelations(listed, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstConnectionId);
        Assert.NotNull(fetched);
        AssertProjectRelations(fetched, scope.FirstMcpToolServerId, scope.FirstSkillId, scope.FirstConnectionId);
    }

    [Fact]
    public async Task UserScopedRelations_ForeignUserCannotViewProjectAndOwnerUpdatePreservesForeignRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ProjectAppServiceTestScope.CreateAsync(cancellationToken);
        var project = await scope.Service.CreateAsync(
            CreateProject("Shared project"),
            null,
            null,
            [scope.FirstConnectionId]
        );
        var foreignConnectionId = Guid.CreateVersion7();
        await using (var seedContext = scope.CreateDbContext())
        {
            seedContext.Connections.Add(
                new Connection
                {
                    Id = foreignConnectionId,
                    PluginId = "github",
                    ConnectorId = "github-cloud",
                    AuthSchemeId = "oauth",
                    DisplayName = "Foreign GitHub",
                    Alias = "foreign-github",
                    CreateBy = "other-user",
                }
            );
            seedContext.ProjectConnectionRelations.Add(
                new ProjectConnectionRelation { ProjectId = project!.Id, ConnectionId = foreignConnectionId }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var ownView = await scope.Service.GetForCurrentUserAsync(project!.Id);
        scope.UserInfo.UserId = "other-user";
        var foreignView = await scope.Service.GetForCurrentUserAsync(project.Id);
        scope.UserInfo.UserId = "tester";
        await scope.Service.UpdateAsync(
            project.Id,
            item => item.Description = "Updated",
            null,
            null,
            [scope.SecondConnectionId]
        );

        Assert.NotNull(ownView);
        Assert.Equal(scope.FirstConnectionId, Assert.Single(ownView.ProjectConnectionRelations).ConnectionId);
        Assert.Null(foreignView);
        await using var assertContext = scope.CreateDbContext();
        using var systemScope = UserInfoUtil.PushSystemScope();
        Assert.Equal(
            new[] { foreignConnectionId, scope.SecondConnectionId }.OrderBy(id => id),
            (
                await assertContext
                    .ProjectConnectionRelations.Select(relation => relation.ConnectionId)
                    .ToListAsync(cancellationToken)
            ).OrderBy(id => id)
        );
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
            [scope.FirstConnectionId]
        );
        await using (var usageContext = scope.CreateDbContext())
        {
            usageContext.AgentUsages.Add(
                new AgentUsage
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = created!.Id,
                    ContextId = "context-1",
                    AgentName = "planner",
                    UserId = "tester",
                    RecordedAt = TimeProvider.System.GetUtcNow(),
                    TotalTokenCount = 10,
                }
            );
            await usageContext.SaveChangesAsync(cancellationToken);
        }

        var deleted = await scope.Service.DeleteAsync(created.Id);

        Assert.True(deleted);
        await using var assertContext = scope.CreateDbContext();
        Assert.False(await assertContext.ProjectMcpToolServers.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectSkillRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
        Assert.Equal(
            created.Id,
            Assert.Single(await assertContext.AgentUsages.ToListAsync(cancellationToken)).ProjectId
        );
    }

    [Fact]
    public async Task DeleteAsync_WhenDeletionCoordinatorRejects_DoesNotInvalidateFileSystemCache()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var cache = new RecordingFileSystemCacheInvalidator();
        var service = CreateService(
            dbContext,
            fileSystemCache: cache,
            deletionCoordinator: new RejectingProjectDeletionCoordinator()
        );
        var project = await service.CreateAsync(CreateProject("Project A"));

        // Act
        var deleted = await service.DeleteAsync(project!.Id);

        // Assert
        Assert.False(deleted);
        Assert.Empty(cache.InvalidatedProjectIds);
        Assert.NotNull(await service.GetForCurrentUserAsync(project.Id));
    }

    [Fact]
    public async Task CreateAsync_WhenWorkspaceDoesNotExist_CreatesWorkspaceDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.CreateVersion7().ToString("N"));
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
                }
            );

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

    private static ProjectAppService CreateService(
        AgwDbContext dbContext,
        TestUserInfoService? userInfo = null,
        IProjectFileSystemCacheInvalidator? fileSystemCache = null,
        IProjectDeletionCoordinator? deletionCoordinator = null
    )
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        userInfo ??= new TestUserInfoService();

        return new ProjectAppService(
            projectRepository,
            new EfRepository<ProjectMcpServerRelation>(dbContext),
            new TestAgentCatalogFacade(new EfRepository<McpServer>(dbContext)),
            new EfRepository<ProjectSkillRelation>(dbContext),
            new SkillReferenceFacade(dbContext, userInfo),
            new EfRepository<ProjectConnectionRelation>(dbContext),
            new ConnectionReferenceFacade(dbContext, userInfo),
            deletionCoordinator ?? TestProjectPersistence.CreateDeletionCoordinator(dbContext),
            dbContext,
            new ProjectDomainService(TimeProvider.System),
            new ProjectResolver(projectRepository, userInfo),
            userInfo,
            fileSystemCache
        );
    }

    private static Project CreateProject(string name) =>
        new()
        {
            Name = name,
            Type = ProjectType.UserDefined,
            Workspace = Path.GetTempPath(),
        };

    private static void AssertProjectRelations(Project project, Guid mcpToolServerId, Guid skillId, Guid connectionId)
    {
        Assert.Equal(mcpToolServerId, Assert.Single(project.ProjectMcpToolServers).McpToolServerId);
        Assert.Equal(skillId, Assert.Single(project.ProjectSkillRelations).SkillId);
        Assert.Equal(connectionId, Assert.Single(project.ProjectConnectionRelations).ConnectionId);
    }

    private sealed class RecordingFileSystemCacheInvalidator : IProjectFileSystemCacheInvalidator
    {
        public List<Guid> InvalidatedProjectIds { get; } = [];

        public void Invalidate(Guid projectId) => InvalidatedProjectIds.Add(projectId);
    }

    private sealed class RejectingProjectDeletionCoordinator : IProjectDeletionCoordinator
    {
        public Task<bool> ClearConversationRecordsAsync(
            ProjectConversationDeletionTarget target,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public Task<bool> DeleteConversationAsync(
            ProjectConversationDeletionTarget target,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public Task<bool> DeleteAllConversationsAsync(
            ProjectDeletionTarget target,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public Task<bool> DeleteProjectAsync(
            ProjectDeletionTarget target,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);
    }

    private sealed class ProjectAppServiceTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _dbContext;

        private ProjectAppServiceTestScope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            AgwDbContext dbContext
        )
        {
            _connection = connection;
            _options = options;
            _dbContext = dbContext;
            UserInfo = new TestUserInfoService();
            Service = CreateService(dbContext, UserInfo);
        }

        public ProjectAppService Service { get; }
        public TestUserInfoService UserInfo { get; }
        public Guid FirstMcpToolServerId { get; } = Guid.CreateVersion7();
        public Guid SecondMcpToolServerId { get; } = Guid.CreateVersion7();
        public Guid FirstSkillId { get; } = Guid.CreateVersion7();
        public Guid SecondSkillId { get; } = Guid.CreateVersion7();
        public Guid FirstConnectionId { get; } = Guid.CreateVersion7();
        public Guid SecondConnectionId { get; } = Guid.CreateVersion7();

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
                new McpServer
                {
                    Id = scope.FirstMcpToolServerId,
                    Name = "MCP 1",
                    CreateBy = "tester",
                },
                new McpServer
                {
                    Id = scope.SecondMcpToolServerId,
                    Name = "MCP 2",
                    CreateBy = "tester",
                }
            );
            dbContext.Skills.AddRange(
                new Skill
                {
                    Id = scope.FirstSkillId,
                    Name = "skill-1",
                    Description = "Skill 1",
                    ContentPath = "/skills/1",
                    Kind = SkillKind.Local,
                    CreateBy = "tester",
                },
                new Skill
                {
                    Id = scope.SecondSkillId,
                    Name = "skill-2",
                    Description = "Skill 2",
                    ContentPath = "/skills/2",
                    Kind = SkillKind.Local,
                    CreateBy = "tester",
                }
            );
            dbContext.Connections.AddRange(
                new Connection
                {
                    Id = scope.FirstConnectionId,
                    PluginId = "github",
                    ConnectorId = "github-cloud",
                    AuthSchemeId = "oauth",
                    DisplayName = "Work GitHub",
                    Alias = "work-github",
                    CreateBy = "tester",
                },
                new Connection
                {
                    Id = scope.SecondConnectionId,
                    PluginId = "github",
                    ConnectorId = "github-cloud",
                    AuthSchemeId = "oauth",
                    DisplayName = "Personal GitHub",
                    Alias = "personal-github",
                    CreateBy = "tester",
                }
            );
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
