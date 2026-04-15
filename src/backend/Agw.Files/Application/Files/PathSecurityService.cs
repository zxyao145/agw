using Microsoft.AspNetCore.Hosting;

namespace Agw.Files.Application.Files;

public sealed class PathSecurityService : IPathSecurityService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public PathSecurityService(IWebHostEnvironment webHostEnvironment)
        : this(webHostEnvironment.ContentRootPath)
    {
    }

    public PathSecurityService(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public bool TryResolvePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidatePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(RootPath, path));

            if (!IsUnderRoot(candidatePath))
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

    private bool IsUnderRoot(string candidatePath)
    {
        if (string.Equals(RootPath, candidatePath, PathComparison))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(RootPath, candidatePath);
        if (relativePath == ".")
        {
            return true;
        }

        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", PathComparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }
}
