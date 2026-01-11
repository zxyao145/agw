using DSystem.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    public async Task<IActionResult> ListAsync([FromQuery] string? path)
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

            foreach (var entry in entries)
            {
                var fileInfo = new FileInfo(entry);
                var dirInfo = new DirectoryInfo(entry);

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
}
