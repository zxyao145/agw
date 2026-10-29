using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Agw.Agents;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Infrastructure.Git;
using Agw.Files.Infrastructure.Storage;
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
using Microsoft.Extensions.Logging.Abstractions;

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
                    ["Projects:Workspace:IsolationEnabled"] = "true",
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

    [Fact]
    public async Task ValidateAsync_ManagedProjectPath_AllowsCurrentOwner()
    {
        var project = await CreateProjectAsync(Path.Combine(Root, "custom"), Path.Combine(Root, "additional"));
        var access = _services.GetRequiredService<IProjectWorkspaceAccess>();
        var managedPath = Path.Combine(Root, ".agw", "projects", project.Id.ToString("N"), "nested", "file.txt");

        await access.ValidateAsync([managedPath], cancellationToken: Token);

        Assert.False(Directory.Exists(Path.GetDirectoryName(managedPath)));
    }

    [Fact]
    public async Task ValidateAsync_ManagedProjectPath_RejectsForeignOwnerAsNotFound()
    {
        var project = await CreateProjectAsync(Path.Combine(Root, "custom"), Path.Combine(Root, "additional"));
        var access = _services.GetRequiredService<IProjectWorkspaceAccess>();
        var managedPath = Path.Combine(Root, ".agw", "projects", project.Id.ToString("N"), "file.txt");

        using (PushUser("intruder"))
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                access.ValidateAsync([managedPath], cancellationToken: Token)
            );

            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        }
    }

    [Fact]
    public async Task ValidateAsync_ManagedRootAncestor_IsRejected()
    {
        var access = _services.GetRequiredService<IProjectWorkspaceAccess>();
        var managedRoot = Path.Combine(Root, ".agw", "projects");

        foreach (var path in new[] { managedRoot, Path.Combine(Root, ".agw"), Root, Path.GetPathRoot(Root)! })
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                access.ValidateAsync([path], cancellationToken: Token)
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        }
    }

    [Fact]
    public async Task ValidateAsync_DeletedProjectPath_IsRejectedAsNotFound()
    {
        var project = await CreateProjectAsync(Path.Combine(Root, "custom"), Path.Combine(Root, "additional"));
        await using (var scope = _services.CreateAsyncScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectAppService>();
            Assert.True(await projects.DeleteAsync(project.Id));
        }

        var managedPath = Path.Combine(Root, ".agw", "projects", project.Id.ToString("N"), "file.txt");
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _services
                .GetRequiredService<IProjectWorkspaceAccess>()
                .ValidateAsync([managedPath], cancellationToken: Token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task ResolveSnapshotAsync_ForeignCapturedRoot_IsRejectedBeforeCreatingSelectedRoot()
    {
        var project = await CreateProjectAsync(
            Path.Combine(Root, "snapshot-workspace"),
            Path.Combine(Root, "additional")
        );
        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        Directory.CreateDirectory(foreignRoot);
        var selectedRoot = Path.Combine(Root, "captured-workspace");
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            project.Id,
            selectedRoot,
            [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), foreignRoot)]
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Resolver.ResolveSnapshotAsync(project.Id, snapshot, null, Token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.False(Directory.Exists(selectedRoot));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveAsync_CachedRootSymlinkRetargetedToForeignProject_IsRejected()
    {
        var sameOwnerProject = await CreateProjectAsync(null, Path.Combine(Root, "same-owner-additional"));
        var sameOwnerRoot = Path.Combine(Root, ".agw", "projects", sameOwnerProject.Id.ToString("N"));
        Directory.CreateDirectory(sameOwnerRoot);
        await File.WriteAllTextAsync(Path.Combine(sameOwnerRoot, "owned.txt"), "owned", Token);

        var alias = Path.Combine(Root, "workspace-alias");
        Directory.CreateSymbolicLink(alias, sameOwnerRoot);
        var project = await CreateProjectAsync(alias, Path.Combine(Root, "project-additional"));
        var fileSystem = await Resolver.ResolveAsync(project.Id, Token);
        Assert.Equal("owned", await fileSystem!.ReadAllTextAsync("owned.txt", Token));

        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        Directory.CreateDirectory(foreignRoot);
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "foreign.txt"), "foreign", Token);
        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, foreignRoot);

        var readException = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.ReadAllTextAsync("foreign.txt", Token)
        );
        var resolveException = await Assert.ThrowsAsync<AgwException>(() => Resolver.ResolveAsync(project.Id, Token));

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, readException.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, resolveException.Code);
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveAsync_CachedInternalSymlinkRetargetedToForeignProject_IsRejected()
    {
        var sameOwnerProject = await CreateProjectAsync(null, Path.Combine(Root, "same-owner-additional"));
        var sameOwnerRoot = Path.Combine(Root, ".agw", "projects", sameOwnerProject.Id.ToString("N"));
        Directory.CreateDirectory(sameOwnerRoot);
        await File.WriteAllTextAsync(Path.Combine(sameOwnerRoot, "owned.txt"), "owned", Token);

        var workspace = Path.Combine(Root, "workspace");
        var link = Path.Combine(workspace, "linked");
        Directory.CreateDirectory(workspace);
        Directory.CreateSymbolicLink(link, sameOwnerRoot);
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "project-additional"));
        var fileSystem = await Resolver.ResolveAsync(project.Id, Token);
        Assert.Equal("owned", await fileSystem!.ReadAllTextAsync("linked/owned.txt", Token));

        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        Directory.CreateDirectory(foreignRoot);
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "foreign.txt"), "foreign", Token);
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, foreignRoot);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.ReadAllTextAsync("linked/foreign.txt", Token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task AuthorizedFileSystem_DeleteForeignDirectorySymlink_RemovesLinkAndPreservesTarget()
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        var foreignFile = Path.Combine(foreignRoot, "secret.txt");
        await File.WriteAllTextAsync(foreignFile, "foreign", Token);
        var link = Path.Combine(workspace, "foreign");
        Directory.CreateSymbolicLink(link, foreignRoot);
        var fileSystem = await Resolver.ResolveAsync(project.Id, Token);

        var enumerationException = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await foreach (var _ in fileSystem!.EnumerateAsync("", "*", recursive: false, Token)) { }
        });
        await fileSystem!.DeleteAsync("foreign", recursive: false, Token);

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, enumerationException.Code);
        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(foreignRoot));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignFile, Token));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task FileAppService_DeleteForeignFileSymlink_RemovesLinkAndPreservesTarget()
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        var foreignFile = Path.Combine(foreignRoot, "secret.txt");
        await File.WriteAllTextAsync(foreignFile, "foreign", Token);
        var link = Path.Combine(workspace, "foreign-link");
        File.CreateSymbolicLink(link, foreignFile);

        var result = await Files.DeleteAsync(project.Id, "foreign-link", recursive: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(foreignFile));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignFile, Token));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task FileAppService_MoveForeignFileSymlink_ReturnsEntryMetadataWithoutReadingTarget()
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        var foreignFile = Path.Combine(foreignRoot, "secret.txt");
        await File.WriteAllTextAsync(foreignFile, "foreign", Token);
        var source = Path.Combine(workspace, "foreign-link");
        File.CreateSymbolicLink(source, foreignFile);
        await File.WriteAllTextAsync(Path.Combine(workspace, "occupied.txt"), "occupied", Token);

        var conflict = await Assert.ThrowsAsync<AgwException>(() =>
            Files.MoveAsync(project.Id, "foreign-link", "occupied.txt", overwrite: false, Token)
        );
        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, conflict.Code);
        Assert.True(File.Exists(source));

        var result = await Files.MoveAsync(project.Id, "foreign-link", "moved-link", overwrite: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("moved-link", result.Value!.Path);
        Assert.Equal("file", result.Value.Type);
        Assert.Null(result.Value.Size);
        Assert.Null(result.Value.ModifiedTime);
        Assert.False(File.Exists(source));
        Assert.Equal(foreignFile, new FileInfo(Path.Combine(workspace, "moved-link")).LinkTarget);
        Assert.True(File.Exists(foreignFile));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignFile, Token));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task FileAppService_MoveForeignDirectorySymlink_ReturnsEntryMetadataWithoutReadingTarget()
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        var link = Path.Combine(workspace, "foreign-directory");
        Directory.CreateSymbolicLink(link, foreignRoot);

        var result = await Files.MoveAsync(project.Id, "foreign-directory", "moved-link", overwrite: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("file", result.Value!.Type);
        Assert.Null(result.Value.Size);
        Assert.NotNull(new DirectoryInfo(Path.Combine(workspace, "moved-link")).LinkTarget);
        Assert.True(Directory.Exists(foreignRoot));
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAppService_MoveSameOwnerDirectorySymlink_ReturnsDirectoryMetadata(bool isolationEnabled)
    {
        var targetProject = await CreateProjectAsync(null, Path.Combine(Root, "target-additional"));
        var targetRoot = Path.Combine(Root, ".agw", "projects", targetProject.Id.ToString("N"));
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        var link = Path.Combine(workspace, "directory-link");
        Directory.CreateSymbolicLink(link, targetRoot);

        var files = isolationEnabled ? Files : CreateFilesServiceWithIsolationDisabled();
        var result = await files.MoveAsync(project.Id, "directory-link", "moved-link", overwrite: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("directory", result.Value!.Type);
        Assert.Null(result.Value.Size);
        Assert.NotNull(new DirectoryInfo(Path.Combine(workspace, "moved-link")).LinkTarget);
        Assert.True(Directory.Exists(targetRoot));
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData(true, "file")]
    [InlineData(false, "directory")]
    public async Task FileAppService_MoveRelativeSymlink_ReturnsDestinationTargetMetadata(
        bool sourceTargetIsDirectory,
        string expectedType
    )
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        var sourceParent = Path.Combine(workspace, "source");
        var destinationParent = Path.Combine(workspace, "destination");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        var sourceTarget = Path.Combine(sourceParent, "target");
        var destinationTarget = Path.Combine(destinationParent, "target");
        if (sourceTargetIsDirectory)
        {
            Directory.CreateDirectory(sourceTarget);
            await File.WriteAllTextAsync(destinationTarget, "file", Token);
        }
        else
        {
            await File.WriteAllTextAsync(sourceTarget, "file", Token);
            Directory.CreateDirectory(destinationTarget);
        }

        File.CreateSymbolicLink(Path.Combine(sourceParent, "link"), "target");

        var result = await Files.MoveAsync(project.Id, "source/link", "destination/moved", overwrite: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal(expectedType, result.Value!.Type);
    }

    [Theory(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FileAppService_DanglingSymlink_CanDeleteOrMoveEntry(bool directoryLink, bool move)
    {
        var workspace = Path.Combine(Root, "workspace");
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        var linkPath = Path.Combine(workspace, "dangling-link");
        var targetPath = Path.Combine(Root, "missing-target");
        if (directoryLink)
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        else
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }

        var fileSystem = (ILocalFileSystem)(await Resolver.ResolveAsync(project.Id, Token))!;
        Assert.True(await fileSystem.IsSymbolicLinkAsync("dangling-link", Token));
        Assert.False(await fileSystem.IsSymbolicLinkAsync("missing", Token));

        if (move)
        {
            var result = await Files.MoveAsync(project.Id, "dangling-link", "moved-link", overwrite: false, Token);

            Assert.Equal(FileOperationStatus.Success, result.Status);
            Assert.NotNull(
                new FileInfo(Path.Combine(workspace, "moved-link")).LinkTarget
                    ?? new DirectoryInfo(Path.Combine(workspace, "moved-link")).LinkTarget
            );
            Assert.Null(new FileInfo(linkPath).LinkTarget ?? new DirectoryInfo(linkPath).LinkTarget);
        }
        else
        {
            var result = await Files.DeleteAsync(project.Id, "dangling-link", recursive: false, Token);

            Assert.Equal(FileOperationStatus.Success, result.Status);
            Assert.Null(new FileInfo(linkPath).LinkTarget ?? new DirectoryInfo(linkPath).LinkTarget);
        }

        Assert.False(File.Exists(targetPath));
        Assert.False(Directory.Exists(targetPath));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ListAsync_DiffOnForeignProjectSymlink_IsRejectedBeforeGitCommand()
    {
        var workspace = Path.Combine(Root, "workspace");
        Directory.CreateDirectory(workspace);
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));

        Project foreignProject;
        using (PushUser("intruder"))
        {
            foreignProject = await CreateProjectAsync(null, Path.Combine(Root, "intruder-additional"));
        }

        var foreignRoot = Path.Combine(Root, ".agw", "projects", foreignProject.Id.ToString("N"));
        Directory.CreateDirectory(foreignRoot);
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "secret.txt"), "before", Token);
        await RunGitAsync(foreignRoot, "init", "--quiet");
        await RunGitAsync(foreignRoot, "add", "secret.txt");
        await RunGitAsync(
            foreignRoot,
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "--quiet",
            "-m",
            "initial"
        );
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "secret.txt"), "after", Token);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "foreign"), foreignRoot);

        var resolver = new ProjectScopedFileSystemResolver(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<FilePathLocks>(),
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            NullLogger<LocalFileSystem>.Instance,
            TimeProvider.System,
            TestProjectWorkspaceAccess.Disabled
        );
        var files = new FileAppService(
            resolver,
            new GitCommandService(NullLogger<GitCommandService>.Instance),
            NullLogger<FileAppService>.Instance,
            _services.GetRequiredService<IProjectWorkspaceAccess>()
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            files.ListAsync(project.Id, "foreign", diff: true, recursive: false, Token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task ValidateAsync_PendingManagedProjectPath_AllowsMatchingCreatingProjectId()
    {
        var access = _services.GetRequiredService<IProjectWorkspaceAccess>();
        var pendingProjectId = Guid.CreateVersion7();
        var managedPath = Path.Combine(Root, ".agw", "projects", pendingProjectId.ToString("N"), "file.txt");

        await access.ValidateAsync([managedPath], pendingProjectId, Token);

        Assert.False(Directory.Exists(Path.GetDirectoryName(managedPath)));
    }

    [Fact(SkipUnless = nameof(CanRedirectUserHome), Skip = "The user profile does not follow HOME on Windows.")]
    public async Task ResolveAsync_IsolationDisabled_PreservesExternalSymlinkReads()
    {
        var workspace = Path.Combine(Root, "workspace");
        var external = Path.Combine(Root, "external");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "linked.txt"), "external", Token);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "linked"), external);
        var project = await CreateProjectAsync(workspace, Path.Combine(Root, "additional"));
        var resolver = new ProjectScopedFileSystemResolver(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<FilePathLocks>(),
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            NullLogger<LocalFileSystem>.Instance,
            TimeProvider.System,
            TestProjectWorkspaceAccess.Disabled
        );

        var fileSystem = await resolver.ResolveAsync(project.Id, Token);

        Assert.IsType<LocalFileSystem>(fileSystem);
        Assert.Equal("external", await fileSystem!.ReadAllTextAsync("linked/linked.txt", Token));
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
                Name = $"recreation-{Guid.CreateVersion7():N}",
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

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await error);
        await output;
    }

    private FileAppService CreateFilesServiceWithIsolationDisabled()
    {
        var access = TestProjectWorkspaceAccess.Disabled;
        var resolver = new ProjectScopedFileSystemResolver(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<FilePathLocks>(),
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            NullLogger<LocalFileSystem>.Instance,
            TimeProvider.System,
            access
        );
        return new FileAppService(
            resolver,
            new GitCommandService(NullLogger<GitCommandService>.Instance),
            NullLogger<FileAppService>.Instance,
            access
        );
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
