using System.Security.Claims;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

/// <summary>
/// OIDC 用户的默认项目在保存前设置绝对主目录并创建目录。创建路径按用户目录持久化。
/// An OIDC user's default Projects store absolute primary directories and create them before saving.
/// </summary>
public sealed class UserProjectInitializerWorkspaceTests : IDisposable
{
    private const string FailProjectInsert =
        "CREATE TRIGGER fail_project_insert BEFORE INSERT ON project BEGIN SELECT RAISE(ABORT, 'project save failed'); END;";

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".scratch",
        "user-project-initializer",
        Guid.CreateVersion7().ToString("N")
    );
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "10000")], authenticationType: "Test")
        )
    );

    private string Home => Path.Combine(_root, "home");

    public void Dispose()
    {
        _userScope.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureDefaultsAsync_NewUser_CreatesDefaultDirectoriesForEmptyWorkspace()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        var paths = CreatePaths();

        // Act
        await new UserProjectInitializer(context, paths, CreateWorkspaceAccess(paths)).EnsureDefaultsAsync(token);

        // Assert
        var projects = await context.Projects.ToListAsync(token);
        Assert.Equal(
            [ProjectDefaults.A2AName, ProjectDefaults.DefaultBuiltInName],
            projects.Select(project => project.Name).Order(StringComparer.Ordinal)
        );
        Assert.All(
            projects,
            project =>
            {
                var workspace = Path.Combine(Home, ".agw", project.Name);
                Assert.Equal(workspace, project.Workspace);
                Assert.True(Path.IsPathFullyQualified(project.Workspace!));
                Assert.True(Directory.Exists(workspace));
            }
        );
    }

    [Fact]
    public async Task EnsureDefaultsAsync_IsolationEnabled_UsesProjectIdDirectories()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        var paths = CreatePaths();
        var initializer = new UserProjectInitializer(context, paths, CreateWorkspaceAccess(paths, true));

        await initializer.EnsureDefaultsAsync(token);

        var projects = await context.Projects.ToListAsync(token);
        Assert.All(
            projects,
            project =>
            {
                var workspace = Path.Combine(Home, ".agw", "projects", project.Id.ToString("N"));
                Assert.Equal(workspace, project.Workspace);
                Assert.True(Path.IsPathFullyQualified(project.Workspace!));
                Assert.True(Directory.Exists(workspace));
            }
        );
    }

    [Fact]
    public async Task EnsureDefaultsAsync_ExistingNullWorkspace_PreservesLegacyFallback()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        var legacyProject = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = ProjectDefaults.DefaultBuiltInName,
            Type = ProjectType.DefaultBuiltIn,
            CreateBy = "10000",
        };
        context.Projects.Add(legacyProject);
        await context.SaveChangesAsync(token);
        var paths = CreatePaths();

        await new UserProjectInitializer(context, paths, CreateWorkspaceAccess(paths)).EnsureDefaultsAsync(token);

        var persisted = await context.Projects.SingleAsync(project => project.Id == legacyProject.Id, token);
        Assert.Null(persisted.Workspace);
        Assert.Equal($"~/.agw/projects/{legacyProject.Id:N}", ProjectDefaults.GetDefaultWorkspace(legacyProject.Id));
    }

    [Fact]
    public async Task EnsureDefaultsAsync_SaveFails_DirectoriesWereCreatedBeforeSaving()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        var paths = CreatePaths();
        await context.Database.ExecuteSqlRawAsync(FailProjectInsert, token);

        // Act
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new UserProjectInitializer(context, paths, CreateWorkspaceAccess(paths)).EnsureDefaultsAsync(token)
        );

        // Assert
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(Home, ".agw")).Count());
        await using var verification = new AgwDbContext(CreateOptions(connection));
        Assert.Equal(0, await verification.Projects.CountAsync(token));
    }

    [Fact]
    public async Task EnsureDefaultsAsync_ExistingDefaults_PreservesDirectoryFiles()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        var paths = CreatePaths();
        var initializer = new UserProjectInitializer(context, paths, CreateWorkspaceAccess(paths));
        await initializer.EnsureDefaultsAsync(token);
        var directories = (await context.Projects.ToListAsync(token)).Select(project => project.Workspace!).ToArray();
        foreach (var directory in directories)
        {
            File.WriteAllText(Path.Combine(directory, "keep.txt"), directory);
        }

        // Act
        await initializer.EnsureDefaultsAsync(token);

        // Assert
        Assert.Equal(2, await context.Projects.CountAsync(token));
        Assert.All(
            directories,
            directory => Assert.Equal(directory, File.ReadAllText(Path.Combine(directory, "keep.txt")))
        );
    }

    private AgwDataPaths CreatePaths() => AgwDataPaths.Resolve(Path.Combine(_root, "data"), Home);

    private IProjectWorkspaceAccess CreateWorkspaceAccess(AgwDataPaths paths, bool isolationEnabled = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ProjectWorkspaceOptions.SectionName}:IsolationEnabled"] = isolationEnabled.ToString(),
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddFiles(configuration);
        services.AddProjects(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IProjectWorkspaceAccess>();
    }

    private static async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task<AgwDbContext> CreateContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var context = new AgwDbContext(CreateOptions(connection));
        await context.Database.EnsureCreatedAsync(cancellationToken);
        return context;
    }
}
