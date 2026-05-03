using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files.Application.Files;

public sealed class PathSecurityService : IPathSecurityService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    [ActivatorUtilitiesConstructor]
    public PathSecurityService(IWebHostEnvironment webHostEnvironment)
        : this(webHostEnvironment.ContentRootPath)
    {
    }

    public PathSecurityService(string rootPath)
        : this(rootPath, GetDefaultAdditionalRootPaths())
    {
    }

    public PathSecurityService(string rootPath, params string[] additionalRootPaths)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new AgwException(ErrorCodes.RootPathRequired);
        }

        RootPath = Path.GetFullPath(rootPath);
        var allowedRootPaths = new List<string> { RootPath };
        foreach (var additionalRootPath in additionalRootPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(additionalRootPath))
            {
                continue;
            }

            var fullAdditionalRootPath = Path.GetFullPath(additionalRootPath);
            if (!allowedRootPaths.Contains(fullAdditionalRootPath, PathComparer))
            {
                allowedRootPaths.Add(fullAdditionalRootPath);
            }
        }

        AllowedRootPaths = allowedRootPaths.AsReadOnly();
    }

    /// <summary>
    /// Gets the canonical primary root directory used for resolving relative paths.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the canonical root directories that resolved file paths may remain under.
    /// </summary>
    public IReadOnlyList<string> AllowedRootPaths { get; }

    /// <summary>
    /// Resolves an absolute or relative path and only returns it when it remains under an allowed root.
    /// </summary>
    public bool TryResolvePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var expandedPath = PathUtil.ExpandTilde(path);
            if (string.IsNullOrWhiteSpace(expandedPath))
            {
                return false;
            }

            var candidatePath = Path.IsPathRooted(expandedPath)
                ? Path.GetFullPath(expandedPath)
                : Path.GetFullPath(Path.Combine(RootPath, expandedPath));

            if (!IsUnderAllowedRoot(candidatePath))
            {
                return false;
            }

            resolvedPath = candidatePath;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies containment with relative path semantics so sibling paths that share the root prefix are rejected.
    /// </summary>
    private bool IsUnderAllowedRoot(string candidatePath)
    {
        foreach (var allowedRootPath in AllowedRootPaths)
        {
            if (IsUnderRoot(candidatePath, allowedRootPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderRoot(string candidatePath, string rootPath)
    {
        if (string.Equals(rootPath, candidatePath, PathComparison))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (relativePath == ".")
        {
            return true;
        }

        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", PathComparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static string[] GetDefaultAdditionalRootPaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Array.Empty<string>()
            : new[] { userProfile };
    }
}
