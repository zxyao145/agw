using System.Collections.Concurrent;
using Agw.Files.Abstracts;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Infrastructure.Storage;

public sealed class ProjectScopedFileSystemResolver : IAgwFileSystemResolver, IProjectFileSystemCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FilePathLocks _pathLocks;
    private readonly ILogger<ProjectScopedFileSystemResolver> _logger;
    private readonly ILogger<LocalFileSystem> _fileSystemLogger;
    private readonly TimeProvider _timeProvider;
    private readonly IProjectWorkspaceAccess _workspaceAccess;
    private readonly ConcurrentDictionary<(Guid ProjectId, Guid? DirectoryId, string Path), CachedEntry> _cache = new();

    public ProjectScopedFileSystemResolver(
        IServiceScopeFactory scopeFactory,
        FilePathLocks pathLocks,
        ILogger<ProjectScopedFileSystemResolver> logger,
        ILogger<LocalFileSystem> fileSystemLogger,
        TimeProvider timeProvider,
        IProjectWorkspaceAccess workspaceAccess
    )
    {
        _scopeFactory = scopeFactory;
        _pathLocks = pathLocks;
        _logger = logger;
        _fileSystemLogger = fileSystemLogger;
        _timeProvider = timeProvider;
        _workspaceAccess = workspaceAccess;
    }

    public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
        ResolveAsync(projectId, null, ct);

    public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, Guid? directoryId, CancellationToken ct) =>
        ResolveCoreAsync(projectId, directoryId, null, ct);

    public Task<IAgwFileSystem?> ResolveSnapshotAsync(
        Guid projectId,
        ProjectWorkspaceSnapshot snapshot,
        Guid? directoryId,
        CancellationToken ct
    ) => ResolveCoreAsync(projectId, directoryId, snapshot, ct);

    private async Task<IAgwFileSystem?> ResolveCoreAsync(
        Guid projectId,
        Guid? directoryId,
        ProjectWorkspaceSnapshot? snapshot,
        CancellationToken ct
    )
    {
        if (projectId == Guid.Empty)
        {
            return null;
        }

        // Always reauthorize the Project; an execution snapshot is not an ownership bypass.
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IProjectFileSystemConfigurationProvider>();
        var project = snapshot == null ? await provider.GetAsync(projectId, ct).ConfigureAwait(false) : null;
        var ownerUserId =
            snapshot == null
                ? project?.OwnerUserId
                : await provider.GetOwnerUserIdAsync(projectId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            Invalidate(projectId);
            return null;
        }

        var directories = snapshot?.AdditionalDirectories ?? project?.AdditionalDirectories ?? [];
        var workspacePath = GetWorkspacePath(snapshot?.Workspace ?? project?.Workspace, projectId);
        string[] declaredRoots = [workspacePath, .. directories.Select(static directory => directory.Path)];
        await _workspaceAccess.ValidateAsync(declaredRoots, cancellationToken: ct).ConfigureAwait(false);

        var rootPath = directoryId.HasValue
            ? directories.FirstOrDefault(directory => directory.Id == directoryId.Value)?.Path
            : workspacePath;
        if (rootPath is null)
        {
            return null;
        }
        // 每次解析都准备实际选中的目录；目录重新创建后物理身份改变，缓存的文件系统随之重建。
        // Every resolution prepares the selected directory; a recreated directory has a new physical identity, so the
        // cached file system is rebuilt.
        rootPath = ProjectWorkspacePaths.EnsureCreated(rootPath);

        var key = (projectId, directoryId, rootPath);
        if (
            _cache.TryGetValue(key, out var cached)
            && string.Equals(cached.OwnerUserId, ownerUserId, StringComparison.Ordinal)
            && cached.Directory.IsCurrent()
        )
        {
            return cached.FileSystem;
        }

        var directory = DirectoryIdentitySnapshot.Capture([rootPath]);
        var localFileSystem = new LocalFileSystem(rootPath, _pathLocks, _fileSystemLogger);
        IAgwFileSystem fileSystem = localFileSystem;
        if (_workspaceAccess.IsolationEnabled)
        {
            fileSystem = new AuthorizedLocalFileSystem(localFileSystem, localFileSystem, _workspaceAccess, projectId);
        }
        _logger.LogDebug(
            "Project {ProjectId} directory {DirectoryId} resolved to {Path}",
            projectId,
            directoryId,
            rootPath
        );
        _cache[key] = new CachedEntry(fileSystem, ownerUserId, directory, _timeProvider.GetUtcNow());
        return fileSystem;
    }

    public void Invalidate(Guid projectId)
    {
        foreach (var key in _cache.Keys.Where(key => key.ProjectId == projectId))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string GetWorkspacePath(string? workspacePath, Guid projectId) =>
        string.IsNullOrWhiteSpace(workspacePath) ? $"~/.agw/projects/{projectId:N}" : workspacePath;

    private sealed record CachedEntry(
        IAgwFileSystem FileSystem,
        string OwnerUserId,
        DirectoryIdentitySnapshot Directory,
        DateTimeOffset CreatedAt
    );
}
