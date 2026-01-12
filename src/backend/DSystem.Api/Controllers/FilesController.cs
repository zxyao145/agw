using DSystem.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.IO;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly ILogger<FilesController> _logger;

    public FilesController(ILogger<FilesController> logger)
    {
        _logger = logger;
    }

    [HttpGet("list")]
    public async Task<IActionResult> ListAsync([FromQuery] string? path, [FromQuery] bool onlyModified = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = "Path parameter is required" });
        }

        // Security: Prevent path traversal attacks
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Contains(".."))
        {
            return BadRequest(new { error = "Invalid path" });
        }

        try
        {
            if (!Directory.Exists(normalizedPath))
            {
                return NotFound(new { error = "Directory not found" });
            }

            var entries = Directory.GetFileSystemEntries(normalizedPath);
            var items = new List<FileItem>();

            // Get modified files from git if requested
            HashSet<string>? modifiedFiles = null;
            if (onlyModified)
            {
                modifiedFiles = await GetModifiedFilesAsync(normalizedPath);
                if (modifiedFiles == null || modifiedFiles.Count == 0)
                {
                    // No git repository or no modified files
                    return Ok(new FileListResponse { Items = new List<FileItem>() });
                }
            }

            foreach (var entry in entries)
            {
                var fileInfo = new FileInfo(entry);
                var dirInfo = new DirectoryInfo(entry);

                // If filtering by modified files
                if (onlyModified && modifiedFiles != null)
                {
                    // For files, check if they are in the modified list
                    if (fileInfo.Exists)
                    {
                        if (!modifiedFiles.Contains(entry))
                        {
                            continue; // Skip non-modified files
                        }
                    }
                    // For directories, check if any modified file is inside
                    else if (dirInfo.Exists)
                    {
                        var hasModifiedDescendant = modifiedFiles.Any(f => f.StartsWith(entry + Path.DirectorySeparatorChar));
                        if (!hasModifiedDescendant)
                        {
                            continue; // Skip directories without modified files
                        }
                    }
                }

                var item = new FileItem
                {
                    Name = Path.GetFileName(entry),
                    Path = entry,
                    Type = dirInfo.Exists ? "directory" : "file",
                    Size = fileInfo.Exists ? fileInfo.Length : null,
                    ModifiedTime = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : dirInfo.LastWriteTimeUtc
                };

                items.Add(item);
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

    private async Task<HashSet<string>?> GetModifiedFilesAsync(string directory)
    {
        var gitDirectory = FindGitDirectory(directory);
        if (gitDirectory == null)
        {
            return null;
        }

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    WorkingDirectory = gitDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return null;
            }

            var modifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 4) continue;

                // Git status format: XY filename
                // X = index status, Y = working tree status
                var statusCode = line.Substring(0, 2);
                var filename = line.Substring(3).Trim().Trim('"');

                // Skip untracked files if needed (starts with ??)
                // Include modified (M), added (A), deleted (D), renamed (R), etc.
                if (statusCode.Trim() == "??")
                {
                    continue; // Skip untracked files
                }

                var fullPath = Path.GetFullPath(Path.Combine(gitDirectory, filename));
                modifiedFiles.Add(fullPath);
            }

            return modifiedFiles;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get modified files from git");
            return null;
        }
    }

    [HttpGet("read")]
    public async Task<IActionResult> ReadAsync([FromQuery] string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = "Path parameter is required" });
        }

        // Security: Prevent path traversal attacks
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Contains(".."))
        {
            return BadRequest(new { error = "Invalid path" });
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
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = "Path parameter is required" });
        }

        // Security: Prevent path traversal attacks
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Contains(".."))
        {
            return BadRequest(new { error = "Invalid path" });
        }

        try
        {
            if (!System.IO.File.Exists(normalizedPath))
            {
                return NotFound(new { error = "File not found" });
            }

            // Get git diff for the file
            var gitDirectory = FindGitDirectory(normalizedPath);
            if (gitDirectory == null)
            {
                return BadRequest(new { error = "File is not in a git repository" });
            }

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"diff HEAD \"{normalizedPath}\"",
                    WorkingDirectory = gitDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Git diff failed: {Error}", error);
                return BadRequest(new { error = "Git diff failed", details = error });
            }

            // If no diff (file unchanged), try to get the file from git
            if (string.IsNullOrWhiteSpace(output))
            {
                var gitRootProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse --show-toplevel",
                        WorkingDirectory = gitDirectory,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                gitRootProcess.Start();
                var gitRoot = (await gitRootProcess.StandardOutput.ReadToEndAsync()).Trim();
                await gitRootProcess.WaitForExitAsync();

                var relativePath = Path.GetRelativePath(gitRoot, normalizedPath).Replace("\\", "/");

                var showProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"show HEAD:\"{relativePath}\"",
                        WorkingDirectory = gitDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                showProcess.Start();
                var originalContent = await showProcess.StandardOutput.ReadToEndAsync();
                var showError = await showProcess.StandardError.ReadToEndAsync();
                await showProcess.WaitForExitAsync();

                if (showProcess.ExitCode == 0)
                {
                    return Ok(new
                    {
                        diff = "",
                        message = "No changes detected",
                        unchanged = true,
                        originalContent
                    });
                }
            }

            return Ok(new
            {
                diff = output,
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
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = "Path parameter is required" });
        }

        // Security: Prevent path traversal attacks
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Contains(".."))
        {
            return BadRequest(new { error = "Invalid path" });
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
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = "Path parameter is required" });
        }

        // Security: Prevent path traversal attacks
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Contains(".."))
        {
            return BadRequest(new { error = "Invalid path" });
        }

        try
        {
            if (!System.IO.File.Exists(normalizedPath))
            {
                return NotFound(new { error = "File not found" });
            }

            // Check if file is in a git repository
            var gitDirectory = FindGitDirectory(normalizedPath);
            if (gitDirectory == null)
            {
                return BadRequest(new { error = "File is not in a git repository" });
            }

            // Get git root to construct relative path
            var gitRootProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --show-toplevel",
                    WorkingDirectory = gitDirectory,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            gitRootProcess.Start();
            var gitRoot = (await gitRootProcess.StandardOutput.ReadToEndAsync()).Trim();
            await gitRootProcess.WaitForExitAsync();

            if (gitRootProcess.ExitCode != 0)
            {
                return BadRequest(new { error = "Failed to get git root directory" });
            }

            var relativePath = Path.GetRelativePath(gitRoot, normalizedPath).Replace("\\", "/");

            // Check if file has modifications
            var statusProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"status --porcelain \"{relativePath}\"",
                    WorkingDirectory = gitDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            statusProcess.Start();
            var statusOutput = await statusProcess.StandardOutput.ReadToEndAsync();
            await statusProcess.WaitForExitAsync();

            if (statusProcess.ExitCode != 0)
            {
                return BadRequest(new { error = "Failed to check git status" });
            }

            if (string.IsNullOrWhiteSpace(statusOutput))
            {
                return Ok(new { success = false, message = "File has no modifications to reset" });
            }

            // Reset the file using git checkout
            var resetProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"checkout HEAD -- \"{relativePath}\"",
                    WorkingDirectory = gitDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            resetProcess.Start();
            var resetOutput = await resetProcess.StandardOutput.ReadToEndAsync();
            var resetError = await resetProcess.StandardError.ReadToEndAsync();
            await resetProcess.WaitForExitAsync();

            if (resetProcess.ExitCode != 0)
            {
                _logger.LogError("Git reset failed: {Error}", resetError);
                return StatusCode(500, new { error = "Git reset failed", details = resetError });
            }

            _logger.LogInformation("Reset file to HEAD: {Path}", normalizedPath);
            return Ok(new { success = true, message = "File reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting file: {Path}", normalizedPath);
            return StatusCode(500, new { error = "Failed to reset file", details = ex.Message });
        }
    }

    private string? FindGitDirectory(string filePath)
    {
        string? directory;
        if (Directory.Exists(filePath))
        {
            directory = filePath;
        }
        else
        {
            directory = Path.GetDirectoryName(filePath);
        }


        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
            {
                return directory;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }
}
