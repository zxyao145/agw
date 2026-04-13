using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Services;
using Agw.Tasks.Application.Files;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Tasks.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly ILogger<FilesController> _logger;
    private readonly IGitCommandService _gitCommandService;
    private readonly IPathSecurityService _pathSecurityService;

    public FilesController(
        ILogger<FilesController> logger,
        IGitCommandService gitCommandService,
        IPathSecurityService pathSecurityService)
    {
        _logger = logger;
        _gitCommandService = gitCommandService;
        _pathSecurityService = pathSecurityService;
    }

    private bool TryResolveRequiredPath(string? path, out string normalizedPath, out IActionResult? errorResult)
    {
        normalizedPath = string.Empty;
        errorResult = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorResult = BadRequest(new { error = "Path parameter is required" });
            return false;
        }

        if (!_pathSecurityService.TryResolvePath(path, out normalizedPath))
        {
            errorResult = BadRequest(new { error = "Invalid path" });
            return false;
        }

        return true;
    }

    [HttpGet("list")]
    public async Task<IActionResult> ListAsync([FromQuery] string? path, [FromQuery] bool diff = false, [FromQuery] bool recursive = false)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        try
        {
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
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied reading directory: {Path}", normalizedPath);
            return StatusCode(403, new { error = "Access denied", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading directory: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to read directory", details = ex.Message });
        }
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
    public async Task<IActionResult> ReadAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        try
        {
            if (!System.IO.File.Exists(normalizedPath))
            {
                return NotFound(new { error = "File not found" });
            }

            var content = await System.IO.File.ReadAllTextAsync(normalizedPath);
            return Ok(content);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied reading file: {Path}", normalizedPath);
            return StatusCode(403, new { error = "Access denied", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to read file", details = ex.Message });
        }
    }

    [HttpGet("diff")]
    public async Task<IActionResult> DiffAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        try
        {
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting git diff: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to get git diff", details = ex.Message });
        }
    }

    [HttpDelete("delete")]
    public async Task<IActionResult> DeleteAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        try
        {
            if (System.IO.File.Exists(normalizedPath))
            {
                System.IO.File.Delete(normalizedPath);
                _logger.LogInformation("Deleted file: {Path}", normalizedPath);
                return Ok(new { success = true, message = "File deleted successfully" });
            }
            else if (Directory.Exists(normalizedPath))
            {
                Directory.Delete(normalizedPath, recursive: true);
                _logger.LogInformation("Deleted directory: {Path}", normalizedPath);
                return Ok(new { success = true, message = "Directory deleted successfully" });
            }
            else
            {
                return NotFound(new { error = "File or directory not found" });
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied deleting: {Path}", normalizedPath);
            return StatusCode(403, new { error = "Access denied", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to delete", details = ex.Message });
        }
    }

    [HttpPost("reset")]
    public async Task<IActionResult> ResetAsync([FromQuery] string? path)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        try
        {
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting file: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to reset file", details = ex.Message });
        }
    }

    private static HashSet<string> IgnoreDir = new HashSet<string>()
    {
        "node_modules",
    };

    private static HashSet<string> IgnoreFiles = new HashSet<string>()
    {
        "tmpclaude*",
    };

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

        // Search files in current directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(currentPath, $"*{keyword}*"))
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

                results.Add(new FileSearchResult
                {
                    FullPath = fileInfo.FullName,
                    RelativePath = Path.GetRelativePath(rootPath, fileInfo.FullName),
                    Type = "file"
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }

        // Recursively search subdirectories
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(currentPath))
            {
                var dirName = new DirectoryInfo(dir).Name;
                if (!dirName.StartsWith("."))
                {
                    SearchFilesRecursive(rootPath, dir, keyword, limit, results);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }

    private void SearchFilesNonRecursive(string rootPath, string keyword, int limit, List<FileSearchResult> results)
    {
        // Search files in current directory only
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, $"*{keyword}*"))
            {
                var fileInfo = new FileInfo(file);
                results.Add(new FileSearchResult
                {
                    FullPath = fileInfo.FullName,
                    RelativePath = Path.GetRelativePath(rootPath, fileInfo.FullName),
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

        try
        {
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
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied searching directory: {Path}", normalizedPath);
            return Task.FromResult<IActionResult>(StatusCode(403, new { error = "Access denied", details = ex.Message }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching directory: {Path}", normalizedPath);
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Failed to search directory", details = ex.Message }));
        }
    }
}
