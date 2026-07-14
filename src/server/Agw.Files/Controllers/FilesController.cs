using Agw.Files.Application.Files;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly ILogger<FilesController> _logger;
    private readonly IGitCommandService _gitCommandService;
    private readonly IFilePathRequestValidator _pathValidator;

    public FilesController(
        ILogger<FilesController> logger,
        IGitCommandService gitCommandService,
        IFilePathRequestValidator pathValidator)
    {
        _logger = logger;
        _gitCommandService = gitCommandService;
        _pathValidator = pathValidator;
    }

    private bool TryResolveRequiredPath(string? path, out string normalizedPath, out IActionResult? errorResult)
    {
        var validation = _pathValidator.ValidateRequiredPath(path);
        normalizedPath = validation.ResolvedPath;
        errorResult = null;

        if (!validation.IsValid)
        {
            errorResult = BadRequest(new { error = validation.ErrorMessage });
            return false;
        }

        if (ControllerContext.HttpContext != null)
        {
            ControllerContext.HttpContext.Items[FileEndpointExceptionMappingMiddleware.ResolvedPathItemKey] = normalizedPath;
        }

        return true;
    }

    [HttpGet("list")]
    [ProducesResponseType(typeof(FileListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync([FromQuery] string? path, [FromQuery] bool diff = false, [FromQuery] bool recursive = false)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        if (!Directory.Exists(normalizedPath))
        {
            return NotFound(new { error = "Directory not found" });
        }

        // If all=true and onlyModified=true, return all changed files recursively
        if (recursive && diff)
        {
            return await GetAllChangedFilesAsync(normalizedPath);
        }

        // Default behavior: list direct children only
        var entries = Directory.GetFileSystemEntries(normalizedPath);
        var items = new List<FileItem>();

        // Get changed files from git if requested
        GitChangedFiles? changedFiles = null;
        if (diff)
        {
            changedFiles = await _gitCommandService.GetChangedFilesAsync(normalizedPath);
            if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
            {
                // No git repository or no changed files
                return Ok(new FileListResponse { Items = new List<FileItem>() });
            }
        }

        foreach (var entry in entries)
        {
            var fileInfo = new FileInfo(entry);
            var dirInfo = new DirectoryInfo(entry);

            // If filtering by changed files
            if (diff && changedFiles != null)
            {
                // For files, check if they are in the changed list
                if (fileInfo.Exists)
                {
                    if (!changedFiles.FileStatuses.ContainsKey(entry))
                    {
                        continue; // Skip unchanged files
                    }
                }
                // For directories, check if any changed file is inside
                else if (dirInfo.Exists)
                {
                    var hasChangedDescendant = changedFiles.FileStatuses.Keys.Any(f => f.StartsWith(entry + Path.DirectorySeparatorChar));
                    if (!hasChangedDescendant)
                    {
                        continue; // Skip directories without changed files
                    }
                }
            }

            var item = new FileItem
            {
                Name = Path.GetFileName(entry),
                Path = entry,
                Type = dirInfo.Exists ? "directory" : "file",
                Size = fileInfo.Exists ? fileInfo.Length : null,
                ModifiedTime = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : dirInfo.LastWriteTimeUtc,
                GitStatus = changedFiles?.FileStatuses.GetValueOrDefault(entry)
            };

            items.Add(item);
        }

        // Add deleted files (they don't exist in filesystem but are tracked by git)
        if (diff && changedFiles != null)
        {
            foreach (var deletedFile in changedFiles.DeletedFiles)
            {
                // Only include deleted files that would be in this directory
                var deletedDir = Path.GetDirectoryName(deletedFile);
                if (string.Equals(deletedDir, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new FileItem
                    {
                        Name = Path.GetFileName(deletedFile),
                        Path = deletedFile,
                        Type = "file",
                        Size = null,
                        ModifiedTime = null,
                        GitStatus = "deleted"
                    });
                }
            }
        }

        // Sort: directories first, then by name
        items = items
            .OrderBy(x => x.Type == "file")
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new FileListResponse { Items = items });
    }

    private async Task<IActionResult> GetAllChangedFilesAsync(string directoryPath)
    {
        var changedFiles = await _gitCommandService.GetChangedFilesAsync(directoryPath);
        if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
        {
            // No git repository or no changed files
            return Ok(new FileListResponse { Items = new List<FileItem>() });
        }

        var items = new List<FileItem>();

        // Add all changed files under the specified directory
        foreach (var (filePath, status) in changedFiles.FileStatuses)
        {
            // Check if the file is under the specified directory
            if (!filePath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip if it's exactly the directory itself
            if (string.Equals(filePath, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // For deleted files or files that don't exist, add them with limited info
            if (!System.IO.File.Exists(filePath))
            {
                items.Add(new FileItem
                {
                    Name = Path.GetFileName(filePath),
                    Path = filePath,
                    Type = "file",
                    Size = null,
                    ModifiedTime = null,
                    GitStatus = status
                });
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            items.Add(new FileItem
            {
                Name = fileInfo.Name,
                Path = filePath,
                Type = "file",
                Size = fileInfo.Length,
                ModifiedTime = fileInfo.LastWriteTimeUtc,
                GitStatus = status
            });
        }

        // Sort by path
        items = items
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new FileListResponse { Items = items });
    }

    [HttpGet("read")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReadAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        if (!System.IO.File.Exists(normalizedPath))
        {
            return NotFound(new { error = "File not found" });
        }

        var content = await System.IO.File.ReadAllTextAsync(normalizedPath);
        return Ok(content);
    }

    [HttpGet("diff")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiffAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        if (!System.IO.File.Exists(normalizedPath))
        {
            return NotFound(new { error = "File not found" });
        }

        var result = await _gitCommandService.GetDiffAsync(normalizedPath);
        if (!result.Success)
        {
            _logger.LogWarning("Git diff failed: {Error}", result.Error);
            return BadRequest(new { error = "Git diff failed", details = result.Error });
        }

        if (result.Unchanged)
        {
            return Ok(new
            {
                diff = "",
                message = "No changes detected",
                unchanged = true,
                originalContent = result.OriginalContent
            });
        }

        return Ok(new
        {
            diff = result.Diff,
            unchanged = false
        });
    }

    [HttpDelete("delete")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        if (System.IO.File.Exists(normalizedPath))
        {
            System.IO.File.Delete(normalizedPath);
            _logger.LogInformation("Deleted file: {Path}", normalizedPath);
            return Ok(new { success = true, message = "File deleted successfully" });
        }

        if (Directory.Exists(normalizedPath))
        {
            Directory.Delete(normalizedPath, recursive: true);
            _logger.LogInformation("Deleted directory: {Path}", normalizedPath);
            return Ok(new { success = true, message = "Directory deleted successfully" });
        }

        return NotFound(new { error = "File or directory not found" });
    }

    [HttpPost("reset")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResetAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        if (!System.IO.File.Exists(normalizedPath))
        {
            return NotFound(new { error = "File not found" });
        }

        var result = await _gitCommandService.ResetFileAsync(normalizedPath);
        if (!result.Success && result.IsClientError)
        {
            return BadRequest(new { error = result.Message });
        }

        if (!result.Success && !string.IsNullOrEmpty(result.Error))
        {
            _logger.LogError("Git reset failed: {Error}", result.Error);
            return StatusCode(500, new { error = "Git reset failed", details = result.Error });
        }

        if (!result.Success)
        {
            return Ok(new { success = false, message = result.Message });
        }

        _logger.LogInformation("Reset file to HEAD: {Path}", normalizedPath);
        return Ok(new { success = true, message = result.Message });
    }

    private static HashSet<string> IgnoreDir = new HashSet<string>()
    {
        "node_modules",
    };

    private static HashSet<string> IgnoreFiles = new HashSet<string>()
    {
        "tmpclaude*",
    };

    private static string GetSearchRelativePath(string rootPath, string path, bool isDirectory)
    {
        var relativePath = Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/');
        return isDirectory ? $"{relativePath.TrimEnd('/')}/" : relativePath;
    }

    private static bool MatchesSearchKeyword(string relativePath, string keyword)
    {
        return relativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchFilesRecursive(
        string rootPath,
        string currentPath,
        string keyword,
        int limit,
        List<FileSearchResult> results
        )
    {
        // Check if current directory name starts with "."
        var currentDirName = new DirectoryInfo(currentPath).Name;
        if (currentDirName.StartsWith("."))
        {
            return; // Skip dot-folders
        }
        if (IgnoreDir.Contains(currentDirName))
        {
            return;
        }

        if (results.Count >= limit)
        {
            return;
        }

        var directories = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(currentPath))
            {
                var dirName = new DirectoryInfo(dir).Name;
                if (dirName.StartsWith(".") || IgnoreDir.Contains(dirName))
                {
                    continue;
                }

                directories.Add(dir);
                var relativePath = GetSearchRelativePath(rootPath, dir, isDirectory: true);
                if (MatchesSearchKeyword(relativePath, keyword))
                {
                    results.Add(new FileSearchResult
                    {
                        FullPath = dir,
                        RelativePath = relativePath,
                        Type = "directory"
                    });

                    if (results.Count >= limit)
                    {
                        return;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }

        // Search files in current directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(currentPath))
            {
                var fileInfo = new FileInfo(file);
                var fileName = fileInfo.Name;
                bool ignore = false;
                foreach (var item in IgnoreFiles)
                {
                    if (item.StartsWith("*") && fileName.EndsWith(item.Substring(1)))
                    {
                        ignore = true;
                        continue;
                    }
                    if (item.EndsWith("*") && fileName.StartsWith(item.Substring(0, item.Length - 1)))
                    {
                        ignore = true;
                        continue;
                    }
                    if (item == fileName)
                    {
                        ignore = true;
                        continue;
                    }
                }
                if (ignore)
                {
                    continue;
                }

                var relativePath = GetSearchRelativePath(rootPath, fileInfo.FullName, isDirectory: false);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchResult
                {
                    FullPath = fileInfo.FullName,
                    RelativePath = relativePath,
                    Type = "file"
                });

                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }

        // Recursively search subdirectories
        try
        {
            foreach (var dir in directories)
            {
                SearchFilesRecursive(rootPath, dir, keyword, limit, results);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }

    private void SearchFilesNonRecursive(string rootPath, string keyword, int limit, List<FileSearchResult> results)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath))
            {
                var dirName = new DirectoryInfo(dir).Name;
                if (dirName.StartsWith(".") || IgnoreDir.Contains(dirName))
                {
                    continue;
                }

                var relativePath = GetSearchRelativePath(rootPath, dir, isDirectory: true);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchResult
                {
                    FullPath = dir,
                    RelativePath = relativePath,
                    Type = "directory"
                });

                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }

        // Search files in current directory only
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath))
            {
                var fileInfo = new FileInfo(file);
                var relativePath = GetSearchRelativePath(rootPath, fileInfo.FullName, isDirectory: false);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchResult
                {
                    FullPath = fileInfo.FullName,
                    RelativePath = relativePath,
                    Type = "file"
                });

                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(FileSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> SearchAsync(
        [FromQuery] string? path,
        [FromQuery] string? keyword,
        [FromQuery] int limit = 10,
        [FromQuery] bool recursive = true)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return Task.FromResult(errorResult!);
        }

        keyword ??= "";

        if (!Directory.Exists(normalizedPath))
        {
            return Task.FromResult<IActionResult>(NotFound(new { error = "Directory not found" }));
        }

        var results = new List<FileSearchResult>();
        if (recursive)
        {
            SearchFilesRecursive(normalizedPath, normalizedPath, keyword, limit, results);
        }
        else
        {
            SearchFilesNonRecursive(normalizedPath, keyword, limit, results);
        }

        // Sort: directories first, then by relative path
        results = results
            .OrderBy(x => x.Type == "file")
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Task.FromResult<IActionResult>(Ok(new FileSearchResponse { Results = results }));
    }
}
