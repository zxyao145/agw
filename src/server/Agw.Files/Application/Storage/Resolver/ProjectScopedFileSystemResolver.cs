using System.Collections.Concurrent;
using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Storage.Resolver;

public sealed class ProjectScopedFileSystemResolver : IAgwFileSystemResolver, IProjectFileSystemCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectScopedFileSystemResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, CachedEntry> _cache = new();

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

    public async Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct)
    {
        if (projectId == Guid.Empty)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var configurationProvider = scope.ServiceProvider.GetRequiredService<IProjectFileSystemConfigurationProvider>();
        var project = await configurationProvider.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project == null)
        {
            _cache.TryRemove(projectId, out _);
            return null;
        }
        if (string.IsNullOrWhiteSpace(project.OwnerUserId))
        {
            _cache.TryRemove(projectId, out _);
            return null;
        }

        var workspace = project.Workspace?.Trim();
        if (_cache.TryGetValue(projectId, out var cached))
        {
            if (
                string.Equals(cached.OwnerUserId, project.OwnerUserId, StringComparison.Ordinal)
                && string.Equals(cached.Workspace, workspace, StringComparison.Ordinal)
            )
            {
                return cached.FileSystem;
            }

            _cache.TryRemove(projectId, out _);
        }

        var fs = CreateForProject(project, projectId);

        _cache[projectId] = new CachedEntry(fs, project.OwnerUserId, workspace, _timeProvider.GetUtcNow());
        return fs;
    }

    public void Invalidate(Guid projectId)
    {
        if (projectId != Guid.Empty)
        {
            _cache.TryRemove(projectId, out _);
        }
    }

    private IAgwFileSystem CreateForProject(ProjectFileSystemConfiguration project, Guid projectId)
    {
        var hasConfiguredWorkspace = !string.IsNullOrWhiteSpace(project.Workspace);
        var rootPath = hasConfiguredWorkspace ? project.Workspace! : $"~/.agw/projects/{projectId:N}";

        if (!hasConfiguredWorkspace)
        {
            Directory.CreateDirectory(PathUtil.ExpandTilde(rootPath));
        }

        _logger.LogInformation("Project {ProjectName} is using local workspace: {Path}", project.Name, rootPath);
        return CreateLocal(rootPath);
    }

    private static LocalFileSystem CreateLocal(string rootPath)
    {
        return new LocalFileSystem(PathUtil.ExpandTilde(rootPath));
    }

    private sealed record CachedEntry(
        IAgwFileSystem FileSystem,
        string? OwnerUserId,
        string? Workspace,
        DateTimeOffset CreatedAt
    );
}
