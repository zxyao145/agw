using System.Collections.Concurrent;

using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Utils;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Storage.Resolver;

public sealed class ProjectScopedFileSystemResolver : IAgwFileSystemResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectScopedFileSystemResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, CachedEntry> _cache = new();

    public ProjectScopedFileSystemResolver(
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectScopedFileSystemResolver> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct)
    {
        if (projectId == Guid.Empty)
        {
            return CreateFallbackLocal();
        }

        if (_cache.TryGetValue(projectId, out var cached))
        {
            return cached.FileSystem;
        }

        var fs = await CreateForProjectAsync(projectId, ct);

        _cache[projectId] = new CachedEntry(fs, _timeProvider.GetUtcNow());
        return fs;
    }

    private async Task<IAgwFileSystem> CreateForProjectAsync(Guid projectId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var configurationProvider = scope.ServiceProvider.GetRequiredService<IProjectFileSystemConfigurationProvider>();

        var project = await configurationProvider.GetAsync(projectId, ct);
        if (project == null)
        {
            _logger.LogWarning("Project {ProjectId} not found, falling back to default local file system.", projectId);
            return CreateFallbackLocal();
        }

        var hasConfiguredWorkspace = !string.IsNullOrWhiteSpace(project.Workspace);
        var rootPath = hasConfiguredWorkspace
            ? project.Workspace!
            : $"~/.agw/{project.Name}";

        if (!hasConfiguredWorkspace)
        {
            Directory.CreateDirectory(PathUtil.ExpandTilde(rootPath));
        }

        _logger.LogInformation(
            "Project {ProjectId} is using local workspace: {Path}",
            projectId,
            rootPath);
        return CreateLocal(rootPath);
    }

    private IAgwFileSystem CreateFallbackLocal()
    {
        var tempWorkspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agw", "temp");

        Directory.CreateDirectory(tempWorkspace);
        return CreateLocal(tempWorkspace);
    }

    private static LocalFileSystem CreateLocal(string rootPath)
    {
        return new LocalFileSystem(PathUtil.ExpandTilde(rootPath));
    }

    private sealed record CachedEntry(IAgwFileSystem FileSystem, DateTimeOffset CreatedAt);
}
