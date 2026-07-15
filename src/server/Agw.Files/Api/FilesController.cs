using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Agw.Files.Api;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly FileAppService _fileAppService;

    public FilesController(FileAppService fileAppService)
    {
        _fileAppService = fileAppService;
    }

    [HttpGet("list")]
    [ProducesResponseType(typeof(FileListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path = "",
        [FromQuery] bool diff = false,
        [FromQuery] bool recursive = false)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.ListAsync(
            projectId,
            path,
            diff,
            recursive,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
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
    public async Task<IActionResult> ReadAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.ReadAsync(
            projectId,
            path,
            RequestCancellationToken);
        return result.Status == FileOperationStatus.Success
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpGet("diff")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiffAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.DiffAsync(
            projectId,
            path,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
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
    public async Task<IActionResult> DeleteAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.DeleteAsync(
            projectId,
            path,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
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
    public async Task<IActionResult> ResetAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.ResetAsync(
            projectId,
            path,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
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
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path = "",
        [FromQuery] string? keyword = null,
        [FromQuery] int limit = 10,
        [FromQuery] bool recursive = true)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.SearchAsync(
            projectId,
            path,
            keyword,
            limit,
            recursive,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
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

    private void TrackRequestedPath(Guid projectId, string? path)
    {
        if (ControllerContext.HttpContext != null)
        {
            ControllerContext.HttpContext.Items[
                FileEndpointExceptionMappingMiddleware.ResolvedPathItemKey] =
                $"{projectId}:{path ?? string.Empty}";
        }
    }

    private IActionResult MapError<T>(FileOperationResult<T> result)
    {
        if (result.Status == FileOperationStatus.NotFound)
        {
            return NotFound(new { error = result.Message });
        }

        if (result.Status == FileOperationStatus.InvalidRequest)
        {
            return result.Details == null
                ? BadRequest(new { error = result.Message })
                : BadRequest(new { error = result.Message, details = result.Details });
        }

        return StatusCode(
            StatusCodes.Status500InternalServerError,
            new
            {
                error = result.Message ?? "Failed to process file request",
                details = result.Details
            });
    }
}
