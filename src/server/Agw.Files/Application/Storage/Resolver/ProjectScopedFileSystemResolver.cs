using System.Collections.Concurrent;
using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Storage.Resolver;

public sealed class ProjectScopedFileSystemResolver : IAgwFileSystemResolver, IProjectFileSystemCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectScopedFileSystemResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(Guid ProjectId, Guid? DirectoryId, string Path), CachedEntry> _cache = new();

    public ProjectScopedFileSystemResolver(
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectScopedFileSystemResolver> logger,
        TimeProvider timeProvider
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
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
        var rootPath = directoryId.HasValue
            ? directories.FirstOrDefault(directory => directory.Id == directoryId.Value)?.Path
            : snapshot?.Workspace ?? project?.Workspace;
        if (directoryId.HasValue && rootPath == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = $"~/.agw/projects/{projectId:N}";
            Directory.CreateDirectory(PathUtil.ExpandTilde(rootPath));
        }
        rootPath = ProjectWorkspacePaths.Normalize(rootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, $"Project directory is unavailable: '{rootPath}'.");
        }

        var key = (projectId, directoryId, rootPath);
        if (
            _cache.TryGetValue(key, out var cached)
            && string.Equals(cached.OwnerUserId, ownerUserId, StringComparison.Ordinal)
        )
        {
            return cached.FileSystem;
        }

        var fileSystem = new LocalFileSystem(rootPath);
        _logger.LogDebug(
            "Project {ProjectId} directory {DirectoryId} resolved to {Path}",
            projectId,
            directoryId,
            rootPath
        );
        _cache[key] = new CachedEntry(fileSystem, ownerUserId, _timeProvider.GetUtcNow());
        return fileSystem;
    }

    public void Invalidate(Guid projectId)
    {
        foreach (var key in _cache.Keys.Where(key => key.ProjectId == projectId))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private sealed record CachedEntry(IAgwFileSystem FileSystem, string OwnerUserId, DateTimeOffset CreatedAt);
}
