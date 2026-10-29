using System.Security.Claims;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Facades;
using Agw.Projects.Application.Facades;
using Agw.Projects.Domain.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Facades;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

/// <summary>
/// 创建或更新项目目录配置时，以及通过 Workspace facade 使用主目录时，缺失的目录被创建；HOME 指向隔离目录，默认 <c>~</c> 路径也在
/// 仓库目录内创建。
/// Missing directories are created when Project directory configuration is created or updated and when the Workspace
/// facade hands out the primary directory; HOME points at an isolated directory, so default <c>~</c> paths are created
/// inside the repository as well.
/// </summary>
[Collection(nameof(IsolatedUserHomeCollection))]
public sealed class ProjectWorkspaceProvisioningTests : IDisposable
{
    private readonly IsolatedUserHome _home = new("project-workspace-provisioning");
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public static bool CanRedirectUserHome => IsolatedUserHome.IsSupported;

    private string Root => _home.Root;

    private Task<Scope> CreateScopeAsync(CancellationToken cancellationToken) =>
        Scope.CreateAsync(cancellationToken, Root);

    public void Dispose()
    {
        _userScope.Dispose();
        _home.Dispose();
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task CreateAsync_MissingCustomWorkspaceAndAdditionalDirectories_CreatesAllDirectories()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "custom", "workspace");
        var first = Path.Combine(Root, "additional", "first");
        var second = Path.Combine(Root, "additional", "nested", "second");

        // Act
        var created = await scope.Service.CreateAsync(Project(workspace, first, second), null, null, null);

        // Assert
        Assert.NotNull(created);
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Equal([first, second], created.AdditionalDirectories.Select(directory => directory.Path));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task UpdateAsync_ChangedWorkspaceAndAdditionalDirectories_CreatesTargetsAndPreservesFiles()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "workspace");
        var kept = Path.Combine(Root, "kept");
        var changed = Path.Combine(Root, "changed");
        var created = (await scope.Service.CreateAsync(Project(workspace, kept, changed), null, null, null))!;
        File.WriteAllText(Path.Combine(workspace, "workspace.txt"), "workspace");
        File.WriteAllText(Path.Combine(kept, "kept.txt"), "kept");
        File.WriteAllText(Path.Combine(changed, "changed.txt"), "changed");
        var nextWorkspace = Path.Combine(Root, "next", "workspace");
        var moved = Path.Combine(Root, "next", "changed");
        var added = Path.Combine(Root, "next", "added");
        var keptDirectory = created.AdditionalDirectories[0];
        var changedDirectory = created.AdditionalDirectories[1];

        // Act
        var updated = (
            await scope.Service.UpdateAsync(
                created.Id,
                project =>
                {
                    project.Workspace = nextWorkspace;
                    project.AdditionalDirectories =
                    [
                        keptDirectory,
                        changedDirectory with
                        {
                            Path = moved,
                        },
                        new ProjectDirectory { Path = added },
                    ];
                },
                null,
                null,
                null
            )
        )!;

        // Assert
        Assert.True(Directory.Exists(nextWorkspace));
        Assert.True(Directory.Exists(moved));
        Assert.True(Directory.Exists(added));
        Assert.Equal("workspace", File.ReadAllText(Path.Combine(workspace, "workspace.txt")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(kept, "kept.txt")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(changed, "changed.txt")));
        Assert.Equal(keptDirectory.Id, updated.AdditionalDirectories[0].Id);
        Assert.NotEqual(changedDirectory.Id, updated.AdditionalDirectories[1].Id);
        Assert.Equal(moved, updated.AdditionalDirectories[1].Path);
        Assert.Equal(added, updated.AdditionalDirectories[2].Path);
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task UpdateAsync_SameConfigurationAfterDirectoriesMoved_RecreatesDirectoriesAndKeepsIds()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "workspace");
        var additional = Path.Combine(Root, "additional");
        var created = (await scope.Service.CreateAsync(Project(workspace, additional), null, null, null))!;
        File.WriteAllText(Path.Combine(workspace, "workspace.txt"), "workspace");
        File.WriteAllText(Path.Combine(additional, "additional.txt"), "additional");
        Directory.Move(workspace, Path.Combine(Root, "workspace-backup"));
        Directory.Move(additional, Path.Combine(Root, "additional-backup"));
        var original = Assert.Single(created.AdditionalDirectories);

        // Act
        var updated = (
            await scope.Service.UpdateAsync(
                created.Id,
                project =>
                {
                    project.Workspace = workspace;
                    project.AdditionalDirectories = [original];
                },
                null,
                null,
                null
            )
        )!;

        // Assert
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(additional));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
        Assert.Empty(Directory.EnumerateFileSystemEntries(additional));
        Assert.Equal(original, Assert.Single(updated.AdditionalDirectories));
        Assert.Equal("additional", File.ReadAllText(Path.Combine(Root, "additional-backup", "additional.txt")));
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData("relative")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("primary")]
    public async Task CreateAsync_InvalidAdditionalDirectory_RejectsBeforeCreatingDirectories(string scenario)
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "workspace");
        var extra = Path.Combine(Root, "extra");
        var project = new Project
        {
            Name = "Directories",
            Type = ProjectType.UserDefined,
            Workspace = workspace,
            AdditionalDirectories = scenario switch
            {
                "relative" => [new ProjectDirectory { Path = "relative/path" }],
                "empty" => [new ProjectDirectory { Path = " " }],
                "duplicate" => [new ProjectDirectory { Path = extra }, new ProjectDirectory { Path = extra + "/." }],
                _ => [new ProjectDirectory { Path = workspace + "/." }],
            },
        };

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Service.CreateAsync(project, null, null, null)
        );

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.False(Directory.Exists(workspace));
        Assert.False(Directory.Exists(extra));
        Assert.Equal(0, await scope.CountProjectsAsync());
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData("primary")]
    [InlineData("additional")]
    public async Task UpdateAsync_DirectoryCreationFails_DoesNotSaveConfiguration(string occupied)
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "workspace");
        var created = (await scope.Service.CreateAsync(Project(workspace), null, null, null))!;
        File.WriteAllText(Path.Combine(workspace, "workspace.txt"), "workspace");
        var file = Path.Combine(Root, "occupied");
        File.WriteAllText(file, "file content");
        var nextWorkspace = occupied == "primary" ? file : Path.Combine(Root, "next");
        var additional = occupied == "additional" ? file : Path.Combine(Root, "additional");

        // Act
        await Assert.ThrowsAnyAsync<IOException>(() =>
            scope.Service.UpdateAsync(
                created.Id,
                project =>
                {
                    project.Workspace = nextWorkspace;
                    project.AdditionalDirectories = [new ProjectDirectory { Path = additional }];
                },
                null,
                null,
                null
            )
        );

        // Assert
        Assert.Equal("file content", File.ReadAllText(file));
        Assert.Equal("workspace", File.ReadAllText(Path.Combine(workspace, "workspace.txt")));
        var persisted = await scope.ReadProjectAsync(created.Id);
        Assert.Equal(workspace, persisted.Workspace);
        Assert.Empty(persisted.AdditionalDirectories);
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task UpdateAsync_OtherUser_DoesNotCreateDirectories()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var created = (await scope.Service.CreateAsync(Project(Path.Combine(Root, "workspace")), null, null, null))!;
        var target = Path.Combine(Root, "foreign");
        var foreignService = scope.CreateService(new TestUserInfoService("intruder"));

        // Act
        var updated = await foreignService.UpdateAsync(
            created.Id,
            project =>
            {
                project.Workspace = target;
                project.AdditionalDirectories = [new ProjectDirectory { Path = Path.Combine(target, "additional") }];
            },
            null,
            null,
            null
        );

        // Assert
        Assert.Null(updated);
        Assert.False(Directory.Exists(target));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task GetWorkspaceAsync_MissingWorkspace_CreatesAndReturnsAbsolutePathEachTime()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "facade", "workspace");
        var created = (await scope.Service.CreateAsync(Project(workspace), null, null, null))!;
        var facade = new ProjectRuntimeFacade(scope.Service, scope.WorkspaceAccess);
        Directory.Move(workspace, Path.Combine(Root, "facade", "backup"));

        // Act
        var first = await facade.GetWorkspaceAsync(created.Id, TestContext.Current.CancellationToken);
        Directory.Delete(workspace);
        var second = await facade.GetWorkspaceAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(workspace, first);
        Assert.Equal(workspace, second);
        Assert.True(Directory.Exists(workspace));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task GetWorkspaceAsync_EmptyWorkspace_CreatesProjectDefaultDirectory()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var projectId = await scope.AddProjectWithoutWorkspaceAsync();
        var facade = new ProjectRuntimeFacade(scope.Service, scope.WorkspaceAccess);
        var expected = Path.Combine(Root, ".agw", "projects", projectId.ToString("N"));

        // Act
        var workspace = await facade.GetWorkspaceAsync(projectId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, workspace);
        Assert.True(Directory.Exists(expected));
        Assert.Null((await scope.ReadProjectAsync(projectId)).Workspace);
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task GetWorkspaceAsync_OtherUser_ReturnsNullWithoutCreatingDirectory()
    {
        // Arrange
        await using var scope = await CreateScopeAsync(TestContext.Current.CancellationToken);
        var workspace = Path.Combine(Root, "owned");
        var created = (await scope.Service.CreateAsync(Project(workspace), null, null, null))!;
        Directory.Delete(workspace);
        var facade = new ProjectRuntimeFacade(
            scope.CreateService(new TestUserInfoService("intruder")),
            scope.WorkspaceAccess
        );

        // Act
        var result = await facade.GetWorkspaceAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
        Assert.False(Directory.Exists(workspace));
    }

    private static Project Project(string workspace, params string[] additionalDirectories) =>
        new()
        {
            Name = "Directories",
            Type = ProjectType.UserDefined,
            Workspace = workspace,
            AdditionalDirectories = additionalDirectories.Select(path => new ProjectDirectory { Path = path }).ToList(),
        };

    private sealed class Scope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _context;
        private readonly AgwDataPaths _dataPaths;
        private readonly ServiceProvider _workspaceServices;

        private Scope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            AgwDbContext context,
            AgwDataPaths dataPaths,
            ServiceProvider workspaceServices
        )
        {
            _connection = connection;
            _options = options;
            _context = context;
            _dataPaths = dataPaths;
            _workspaceServices = workspaceServices;
            Service = CreateService(new TestUserInfoService());
        }

        public ProjectAppService Service { get; }

        public IProjectWorkspaceAccess WorkspaceAccess =>
            _workspaceServices.GetRequiredService<IProjectWorkspaceAccess>();

        public static async Task<Scope> CreateAsync(CancellationToken cancellationToken, string userHomeDirectory)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(
                    new EntityCreatorInterceptor(new TestAuditUserIdProvider(), TimeProvider.System),
                    new EntityModifierInterceptor(new TestAuditUserIdProvider(), TimeProvider.System)
                )
                .UseSnakeCaseNamingConvention()
                .Options;
            var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            var dataPaths = AgwDataPaths.Resolve(
                Path.Combine(userHomeDirectory, "data"),
                userHomeDirectory,
                Path.Combine(userHomeDirectory, "logs")
            );
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();
            services.AddSingleton(dataPaths);
            services.Configure<ProjectWorkspaceOptions>(static _ => { });
            services.AddFiles(configuration);
            return new Scope(connection, options, context, dataPaths, services.BuildServiceProvider());
        }

        public ProjectAppService CreateService(TestUserInfoService userInfo) =>
            new(
                _context,
                new ProjectResourceBindingDomainService(
                    new TestAgentCatalogFacade(new EfRepository<McpServer>(_context)),
                    new SkillReferenceFacade(_context, userInfo),
                    new ConnectionReferenceFacade(_context, userInfo)
                ),
                TestProjectPersistence.CreateDeletionCoordinator(_context),
                new ProjectResolver(_context, userInfo),
                userInfo,
                _dataPaths,
                WorkspaceAccess
            );

        public async Task<Guid> AddProjectWithoutWorkspaceAsync()
        {
            await using var context = new AgwDbContext(_options);
            var project = new Project
            {
                Id = Guid.CreateVersion7(),
                Name = ProjectDefaults.DefaultBuiltInName,
                Type = ProjectType.DefaultBuiltIn,
                CreateBy = "tester",
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return project.Id;
        }

        public async Task<Project> ReadProjectAsync(Guid projectId)
        {
            await using var context = new AgwDbContext(_options);
            return await context.Projects.SingleAsync(
                project => project.Id == projectId,
                TestContext.Current.CancellationToken
            );
        }

        public async Task<int> CountProjectsAsync()
        {
            await using var context = new AgwDbContext(_options);
            return await context.Projects.CountAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _workspaceServices.DisposeAsync();
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
