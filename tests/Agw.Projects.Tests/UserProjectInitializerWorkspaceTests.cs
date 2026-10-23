using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

/// <summary>
/// OIDC 用户的默认项目在保存前创建有效主目录；数据库保存空 <c>Workspace</c>，<c>~</c> 按注入的路径上下文中的用户目录展开。
/// An OIDC user's default Projects get their effective primary directories before saving; the database keeps an empty
/// <c>Workspace</c>, and <c>~</c> expands with the user home of the injected path context.
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

        // Act
        await new UserProjectInitializer(context, CreatePaths()).EnsureDefaultsAsync(token);

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
                Assert.Null(project.Workspace);
                Assert.True(Directory.Exists(Path.Combine(Home, ".agw", "projects", project.Id.ToString("N"))));
            }
        );
    }

    [Fact]
    public async Task EnsureDefaultsAsync_SaveFails_DirectoriesWereCreatedBeforeSaving()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(token);
        await using var context = await CreateContextAsync(connection, token);
        await context.Database.ExecuteSqlRawAsync(FailProjectInsert, token);

        // Act
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new UserProjectInitializer(context, CreatePaths()).EnsureDefaultsAsync(token)
        );

        // Assert
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(Home, ".agw", "projects")).Count());
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
        var initializer = new UserProjectInitializer(context, CreatePaths());
        await initializer.EnsureDefaultsAsync(token);
        var directories = (await context.Projects.ToListAsync(token))
            .Select(project => Path.Combine(Home, ".agw", "projects", project.Id.ToString("N")))
            .ToArray();
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
