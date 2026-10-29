using Agw.Files.Abstracts;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Files.Infrastructure.Storage;

internal sealed class ProjectWorkspaceAccess : IProjectWorkspaceAccess
{
    private const string ResourceNotFoundMessage = "Project was not found.";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly IOptions<ProjectWorkspaceOptions> _options;
    private readonly AgwDataPaths _dataPaths;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectWorkspaceAccess(
        IOptions<ProjectWorkspaceOptions> options,
        AgwDataPaths dataPaths,
        IServiceScopeFactory scopeFactory
    )
    {
        _options = options;
        _dataPaths = dataPaths;
        _scopeFactory = scopeFactory;
    }

    public bool IsolationEnabled => _options.Value.IsolationEnabled;

    public async Task ValidateAsync(
        IReadOnlyList<string> paths,
        Guid? creatingProjectId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!await IsAccessibleCoreAsync(paths, creatingProjectId, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, ResourceNotFoundMessage);
        }
    }

    public Task<bool> IsAccessibleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        IsAccessibleCoreAsync(paths, null, cancellationToken);

    private async Task<bool> IsAccessibleCoreAsync(
        IReadOnlyList<string> paths,
        Guid? creatingProjectId,
        CancellationToken cancellationToken
    )
    {
        if (!IsolationEnabled || paths.Count == 0)
        {
            return true;
        }

        var managedRoot = NormalizePath(Path.Combine(_dataPaths.UserHomeDirectory, ".agw", "projects"));
        var physicalManagedRoot = PhysicalPaths.ResolveContent(managedRoot);
        var projectIds = new HashSet<Guid>();
        foreach (var path in paths)
        {
            var normalizedPath = NormalizePath(path);
            if (
                !TryAddManagedProjectId(normalizedPath, managedRoot, projectIds)
                || !TryAddManagedProjectId(
                    PhysicalPaths.ResolveContent(normalizedPath),
                    physicalManagedRoot,
                    projectIds
                )
            )
            {
                return false;
            }
        }

        if (creatingProjectId is Guid pendingProjectId && pendingProjectId != Guid.Empty)
        {
            projectIds.Remove(pendingProjectId);
        }
        if (projectIds.Count == 0)
        {
            return true;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IProjectFileSystemConfigurationProvider>();
        foreach (var projectId in projectIds)
        {
            if (
                string.IsNullOrWhiteSpace(
                    await provider.GetOwnerUserIdAsync(projectId, cancellationToken).ConfigureAwait(false)
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(PathUtil.ExpandTilde(path.Trim(), _dataPaths.UserHomeDirectory))
        );

    private static bool TryAddManagedProjectId(string path, string managedRoot, HashSet<Guid> projectIds)
    {
        if (IsSameOrBelow(path, managedRoot))
        {
            var relativePath = Path.GetRelativePath(managedRoot, path);
            if (relativePath == ".")
            {
                return false;
            }

            var projectSegment = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )[0];
            if (!Guid.TryParseExact(projectSegment, "N", out var projectId))
            {
                return false;
            }

            projectIds.Add(projectId);
            return true;
        }

        if (IsSameOrBelow(managedRoot, path))
        {
            return false;
        }

        return true;
    }

    private static bool IsSameOrBelow(string path, string parent)
    {
        var relativePath = Path.GetRelativePath(parent, path);
        return relativePath == "." || (!Path.IsPathRooted(relativePath) && !IsParentPath(relativePath));
    }

    private static bool IsParentPath(string path) =>
        path == ".."
        || path.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
        || path.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
}
