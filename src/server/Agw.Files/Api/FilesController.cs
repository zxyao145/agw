using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Files.Api;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly FileAppService _fileAppService;
    private readonly IFilePathRequestValidator _pathValidator;

    public FilesController(
        FileAppService fileAppService,
        IFilePathRequestValidator pathValidator)
    {
        _fileAppService = fileAppService;
        _pathValidator = pathValidator;
    }

    [HttpGet("list")]
    [ProducesResponseType(typeof(FileListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string? path,
        [FromQuery] bool diff = false,
        [FromQuery] bool recursive = false)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        var result = await _fileAppService.ListAsync(
            normalizedPath,
            diff,
            recursive,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return result.Status == FileOperationStatus.NotFound
                ? NotFound(new { error = result.Message })
                : MapUnexpectedError(result);
        }

        var items = result.Value!.Items
            .Select(entry => new FileItem
            {
                Name = entry.Name,
                Path = entry.Path,
                Type = entry.Type,
                Size = entry.Size,
                ModifiedTime = entry.ModifiedTime,
                GitStatus = entry.GitStatus
            })
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

        var result = await _fileAppService.ReadAsync(normalizedPath, RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return result.Status == FileOperationStatus.NotFound
                ? NotFound(new { error = result.Message })
                : MapUnexpectedError(result);
        }

        return Ok(result.Value);
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

        var result = await _fileAppService.DiffAsync(normalizedPath, RequestCancellationToken);
        if (result.Status == FileOperationStatus.NotFound)
        {
            return NotFound(new { error = result.Message });
        }

        if (result.Status == FileOperationStatus.InvalidRequest)
        {
            return BadRequest(new { error = result.Message, details = result.Details });
        }

        if (result.Status != FileOperationStatus.Success)
        {
            return MapUnexpectedError(result);
        }

        if (result.Value!.Unchanged)
        {
            return Ok(new
            {
                diff = "",
                message = "No changes detected",
                unchanged = true,
                originalContent = result.Value.OriginalContent
            });
        }

        return Ok(new
        {
            diff = result.Value.Diff,
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

        var result = await _fileAppService.DeleteAsync(normalizedPath, RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return result.Status == FileOperationStatus.NotFound
                ? NotFound(new { error = result.Message })
                : MapUnexpectedError(result);
        }

        return Ok(new
        {
            success = result.Value!.Success,
            message = result.Value.Message
        });
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

        var result = await _fileAppService.ResetAsync(normalizedPath, RequestCancellationToken);
        if (result.Status == FileOperationStatus.NotFound)
        {
            return NotFound(new { error = result.Message });
        }

        if (result.Status == FileOperationStatus.InvalidRequest)
        {
            return BadRequest(new { error = result.Message });
        }

        if (result.Status == FileOperationStatus.Failure)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.Message, details = result.Details });
        }

        if (result.Status != FileOperationStatus.Success)
        {
            return MapUnexpectedError(result);
        }

        return Ok(new
        {
            success = result.Value!.Success,
            message = result.Value.Message
        });
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(FileSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SearchAsync(
        [FromQuery] string? path,
        [FromQuery] string? keyword,
        [FromQuery] int limit = 10,
        [FromQuery] bool recursive = true)
    {
        if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
        {
            return errorResult!;
        }

        var result = await _fileAppService.SearchAsync(
            normalizedPath,
            keyword,
            limit,
            recursive,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return result.Status == FileOperationStatus.NotFound
                ? NotFound(new { error = result.Message })
                : MapUnexpectedError(result);
        }

        var results = result.Value!.Results
            .Select(entry => new FileSearchResult
            {
                FullPath = entry.FullPath,
                RelativePath = entry.RelativePath,
                Type = entry.Type
            })
            .ToList();
        return Ok(new FileSearchResponse { Results = results });
    }

    private CancellationToken RequestCancellationToken =>
        ControllerContext.HttpContext?.RequestAborted ?? CancellationToken.None;

    private bool TryResolveRequiredPath(
        string? path,
        out string normalizedPath,
        out IActionResult? errorResult)
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
            ControllerContext.HttpContext.Items[
                FileEndpointExceptionMappingMiddleware.ResolvedPathItemKey] = normalizedPath;
        }

        return true;
    }

    private IActionResult MapUnexpectedError<T>(FileOperationResult<T> result)
    {
        return StatusCode(
            StatusCodes.Status500InternalServerError,
            new
            {
                error = result.Message ?? "Failed to process file request",
                details = result.Details
            });
    }
}
