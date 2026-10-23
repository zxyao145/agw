using System.Security.Claims;
using System.Text;
using Agw.Agents;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Providers;
using Agw.Settings;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Skills;
using Agw.Tools;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files.Tests;

/// <summary>
/// 通过生产 DI、真实项目记录和 resolver 验证：每次解析主目录或附加目录前创建缺失的目录，目录重新创建后缓存的文件系统和受限文件系统
/// 改用当前目录。HOME 指向隔离目录，默认 <c>~</c> 路径同样在仓库目录内创建。
/// Verified through production DI, real Project records, and the resolver: missing primary or additional directories
/// are created before every resolution, and cached and confined file systems switch to the current directory after it
/// is recreated. HOME points at an isolated directory, so default <c>~</c> paths are created inside the repository.
/// </summary>
[Collection(nameof(IsolatedUserHomeCollection))]
public sealed class ProjectWorkspaceRecreationTests : IAsyncDisposable
{
    private const string Owner = "owner";

    private readonly IsolatedUserHome _home = new("project-workspace-recreation");
    private readonly IDisposable _userScope = PushUser(Owner);
    private readonly ServiceProvider _services;

    public ProjectWorkspaceRecreationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "sqlite",
                    ["Database:ConnectionString"] = $"Data Source={Path.Combine(Root, "agw.db")};Pooling=False",
                    ["Execution:Provider"] = "InProcess",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(Root, "data"), Root));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddScoped<IEntityAuditUserIdProvider, AuditUserIdProvider>();
        services
            .AddTools(configuration)
            .AddAgents(configuration)
            .AddFiles(configuration)
            .AddInfrastructure(configuration)
            .AddJobs(configuration, new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: false))
            .AddProviders(configuration)
            .AddSkills(configuration)
            .AddProjects(configuration)
            .AddAuth()
            .AddSettings()
            .AddIntegrations(configuration);
        _services = services.BuildServiceProvider();
    }

    public static bool CanRedirectUserHome => IsolatedUserHome.IsSupported;

    private string Root => _home.Root;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IAgwFileSystemResolver Resolver => _services.GetRequiredService<IAgwFileSystemResolver>();

    private FileAppService Files => _services.GetRequiredService<FileAppService>();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _userScope.Dispose();
        _home.Dispose();
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData("built-in")]
    [InlineData("custom")]
    [InlineData("additional")]
    public async Task ListAsync_MissingDirectory_CreatesSelectedDirectoryBeforeListing(string kind)
    {
        // Arrange
        var custom = Path.Combine(Root, "custom", "workspace");
        var additional = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(kind == "built-in" ? null : custom, additional);
        var expected = kind switch
        {
            "built-in" => Path.Combine(Root, ".agw", "projects", project.Id.ToString("N")),
            "custom" => custom,
            _ => additional,
        };
        Directory.Delete(expected);
        Guid? directoryId = kind == "additional" ? project.AdditionalDirectories[0].Id : null;

        // Act
        var list = await Files.ListAsync(project.Id, "", false, false, Token, directoryId);

        // Assert
        Assert.Equal(FileOperationStatus.Success, list.Status);
        Assert.Empty(list.Value!.Items);
        Assert.True(Directory.Exists(expected));
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_DirectoryMovedAfterCaching_UsesRecreatedDirectory(bool additional)
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var additionalPath = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(primary, additionalPath);
        Guid? directoryId = additional ? project.AdditionalDirectories[0].Id : null;
        var directory = additional ? additionalPath : primary;
        var backup = Path.Combine(Root, "backup");
        await WriteAsync(project.Id, "old.txt", "old", directoryId);
        Assert.Single((await Files.ListAsync(project.Id, "", false, false, Token, directoryId)).Value!.Items);

        // Act
        Directory.Move(directory, backup);
        var recreated = await Files.ListAsync(project.Id, "", false, false, Token, directoryId);
        await WriteAsync(project.Id, "new.txt", "new", directoryId);
        var read = await Files.ReadAsync(project.Id, "new.txt", Token, directoryId);

        // Assert
        Assert.Empty(recreated.Value!.Items);
        Assert.Equal("new", read.Value);
        Assert.Equal("new", File.ReadAllText(Path.Combine(directory, "new.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "old.txt")));
        Assert.Equal(["old.txt"], Directory.EnumerateFiles(backup).Select(Path.GetFileName));
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveSnapshotAsync_DirectoryRecreatedByExecutionPreparation_RebuildsCachedFileSystem(
        bool additional
    )
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var additionalPath = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(primary, additionalPath);
        var snapshot = CreateSnapshot(project);
        Guid? directoryId = additional ? project.AdditionalDirectories[0].Id : null;
        var directory = additional ? additionalPath : primary;
        var cached = await Resolver.ResolveSnapshotAsync(project.Id, snapshot, directoryId, Token);
        Assert.Same(cached, await Resolver.ResolveSnapshotAsync(project.Id, snapshot, directoryId, Token));
        Directory.Move(directory, Path.Combine(Root, "backup"));
        ProjectWorkspacePaths.EnsureAvailable(snapshot);

        // Act
        var current = (await Resolver.ResolveSnapshotAsync(project.Id, snapshot, directoryId, Token))!;
        await current.WriteAllTextAsync("after.txt", "after", Token);

        // Assert
        Assert.NotSame(cached, current);
        Assert.Equal("after", File.ReadAllText(Path.Combine(directory, "after.txt")));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ProjectAgentFileStore_OnlyAdditionalDirectoryRecreated_RefreshesPrimaryConfinedCache()
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var additional = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(primary, additional);
        Directory.CreateSymbolicLink(Path.Combine(primary, "linked"), additional);
        var store = new ProjectAgentFileStore(Resolver, project.Id, null, CreateSnapshot(project));
        await store.WriteAsync("linked/before.txt", "before", Token);
        var primaryFileSystem = await Resolver.ResolveAsync(project.Id, Token);
        Directory.Move(additional, Path.Combine(Root, "backup"));

        // Act
        await store.WriteAsync("linked/after.txt", "after", Token);

        // Assert
        Assert.Same(primaryFileSystem, await Resolver.ResolveAsync(project.Id, Token));
        Assert.Equal("after", File.ReadAllText(Path.Combine(additional, "after.txt")));
        Assert.Equal("after", await store.ReadAsync("linked/after.txt", Token));
        Assert.False(File.Exists(Path.Combine(additional, "before.txt")));
        Assert.True(File.Exists(Path.Combine(Root, "backup", "before.txt")));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveSnapshotAsync_ConfigurationChanged_CapturedDirectoriesStillPrepared()
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var additional = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(primary, additional);
        var snapshot = CreateSnapshot(project);
        var removedId = project.AdditionalDirectories[0].Id;
        var nextPrimary = Path.Combine(Root, "next-primary");
        await UpdateProjectAsync(
            project.Id,
            current =>
            {
                current.Workspace = nextPrimary;
                current.AdditionalDirectories = [];
            }
        );
        Directory.Delete(primary);
        Directory.Delete(additional);

        // Act
        var capturedPrimary = await Resolver.ResolveSnapshotAsync(project.Id, snapshot, null, Token);
        var capturedAdditional = await Resolver.ResolveSnapshotAsync(project.Id, snapshot, removedId, Token);
        var currentAdditional = await Resolver.ResolveAsync(project.Id, removedId, Token);
        var currentPrimary = await Resolver.ResolveAsync(project.Id, Token);

        // Assert
        Assert.NotNull(capturedPrimary);
        Assert.NotNull(capturedAdditional);
        Assert.True(Directory.Exists(primary));
        Assert.True(Directory.Exists(additional));
        Assert.Null(currentAdditional);
        await currentPrimary!.WriteAllTextAsync("current.txt", "current", Token);
        Assert.True(File.Exists(Path.Combine(nextPrimary, "current.txt")));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveAsync_UnknownDirectoryOrForeignUser_DoesNotCreateDirectories()
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var additional = Path.Combine(Root, "additional");
        var project = await CreateProjectAsync(primary, additional);
        var snapshot = CreateSnapshot(project);
        Directory.Delete(primary);
        Directory.Delete(additional);

        // Act
        var unknown = await Resolver.ResolveAsync(project.Id, Guid.CreateVersion7(), Token);
        IAgwFileSystem? foreign;
        IAgwFileSystem? foreignSnapshot;
        using (PushUser("intruder"))
        {
            foreign = await Resolver.ResolveAsync(project.Id, project.AdditionalDirectories[0].Id, Token);
            foreignSnapshot = await Resolver.ResolveSnapshotAsync(project.Id, snapshot, null, Token);
        }

        // Assert
        Assert.Null(unknown);
        Assert.Null(foreign);
        Assert.Null(foreignSnapshot);
        Assert.False(Directory.Exists(primary));
        Assert.False(Directory.Exists(additional));
        var selected = (await Resolver.ResolveAsync(project.Id, project.AdditionalDirectories[0].Id, Token))!;
        await selected.WriteAllTextAsync("selected.txt", "selected", Token);
        Assert.True(File.Exists(Path.Combine(additional, "selected.txt")));
        Assert.False(Directory.Exists(primary));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveAsync_FileOccupiesDirectoryPath_FailsWithoutChangingFile()
    {
        // Arrange
        var primary = Path.Combine(Root, "primary");
        var project = await CreateProjectAsync(primary, Path.Combine(Root, "additional"));
        Directory.Delete(primary);
        File.WriteAllText(primary, "file content");

        // Act
        await Assert.ThrowsAnyAsync<IOException>(() => Files.ListAsync(project.Id, "", false, false, Token));

        // Assert
        Assert.Equal("file content", File.ReadAllText(primary));
    }

    private async Task<Project> CreateProjectAsync(string? workspace, string additional)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await context.Database.EnsureCreatedAsync(Token);
        var projects = scope.ServiceProvider.GetRequiredService<IProjectAppService>();
        var created = await projects.CreateAsync(
            new Project
            {
                Name = "recreation",
                Type = ProjectType.UserDefined,
                Workspace = workspace,
                AdditionalDirectories = [new ProjectDirectory { Path = additional }],
            },
            null,
            null,
            null
        );
        if (workspace == null)
        {
            // 数据库中保存空 Workspace 的内置项目，有效主目录为该项目的默认路径。
            // A built-in Project saved with an empty Workspace uses the Project's default path as its primary directory.
            var entity = await context.Projects.SingleAsync(project => project.Id == created!.Id, Token);
            entity.Workspace = null;
            await context.SaveChangesAsync(Token);
            created = await projects.GetAsync(created!.Id);
        }

        return created!;
    }

    private async Task UpdateProjectAsync(Guid projectId, Action<Project> update)
    {
        await using var scope = _services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectAppService>();
        Assert.NotNull(await projects.UpdateAsync(projectId, update, null, null, null));
    }

    private async Task WriteAsync(Guid projectId, string path, string content, Guid? directoryId)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = await Files.WriteAsync(projectId, path, stream, true, false, null, Token, directoryId);
        Assert.Equal(FileOperationStatus.Success, result.Status);
    }

    private static ProjectWorkspaceSnapshot CreateSnapshot(Project project) =>
        ProjectWorkspacePaths.CreateSnapshot(
            project.Id,
            project.Workspace,
            project.AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(
                directory.Id,
                directory.Path
            ))
        );

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"))
        );

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }
}
