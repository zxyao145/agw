using Agw.Files.Exceptions;
using Agw.Files.Utils;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files.Application.Files;

public sealed class FilePathRequestValidator : IFilePathRequestValidator
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly IReadOnlyList<string> _allowedRootPaths;
    private readonly string _rootPath;

    [ActivatorUtilitiesConstructor]
    public FilePathRequestValidator(IWebHostEnvironment webHostEnvironment)
        : this(webHostEnvironment.ContentRootPath)
    {
    }

    public FilePathRequestValidator(string rootPath)
        : this(rootPath, GetDefaultAdditionalRootPaths())
    {
    }

    public FilePathRequestValidator(string rootPath, params string[] additionalRootPaths)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new AgwFilesException(FilesErrorCode.RootPathRequired, "Root path is required.");
        }

        _rootPath = Path.GetFullPath(rootPath);
        var allowedRootPaths = new List<string> { _rootPath };
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

        _allowedRootPaths = allowedRootPaths.AsReadOnly();
    }

    public FilePathRequestValidationResult ValidateRequiredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FilePathRequestValidationResult.Error("Path parameter is required");
        }

        return TryResolvePath(path, out var resolvedPath)
            ? FilePathRequestValidationResult.Success(resolvedPath)
            : FilePathRequestValidationResult.Error("Invalid path");
    }

    private bool TryResolvePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        try
        {
            var expandedPath = PathUtil.ExpandTilde(path);
            if (string.IsNullOrWhiteSpace(expandedPath))
            {
                return false;
            }

            var candidatePath = Path.IsPathRooted(expandedPath)
                ? Path.GetFullPath(expandedPath)
                : Path.GetFullPath(Path.Combine(_rootPath, expandedPath));

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

    private bool IsUnderAllowedRoot(string candidatePath)
    {
        foreach (var allowedRootPath in _allowedRootPaths)
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
