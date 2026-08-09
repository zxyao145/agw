using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;
using Agw.Files.Exceptions;

using Bens.Results;

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
    [ProducesResponseType(typeof(ApiResult<FileListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
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
                GitStatus = entry.GitStatus,
                GitStagedStatus = entry.GitStagedStatus,
                GitUnstagedStatus = entry.GitUnstagedStatus
            })
            .ToList();
        return ApiResult.Ok(new FileListResponse { Items = items });
    }

    [HttpGet("read")]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
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
            ? ApiResult.Ok(result.Value)
            : MapError(result);
    }

    [HttpGet("diff")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiffAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] string? scope = null)
    {
        TrackRequestedPath(projectId, path);
        var result = await _fileAppService.DiffAsync(
            projectId,
            path,
            scope,
            RequestCancellationToken);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
        }

        if (result.Value!.Unchanged)
        {
            return ApiResult.Ok(new
            {
                diff = "",
                message = "No changes detected",
                unchanged = true,
                originalContent = result.Value.OriginalContent
            });
        }

        return ApiResult.Ok(new
        {
            diff = result.Value.Diff,
            unchanged = false
        });
    }

    [HttpDelete("delete")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
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

        return ApiResult.Ok(new
        {
            success = result.Value!.Success,
            message = result.Value.Message
        });
    }

    [HttpPost("reset")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
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

        return ApiResult.Ok(new
        {
            success = result.Value!.Success,
            message = result.Value.Message
        });
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(ApiResult<FileSearchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
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
        return ApiResult.Ok(new FileSearchResponse { Results = results });
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
            return CreateError(
                FilesErrorCode.ResourceNotFound,
                result.Message ?? "Resource was not found.",
                StatusCodes.Status404NotFound,
                result.Details);
        }

        if (result.Status == FileOperationStatus.InvalidRequest)
        {
            return CreateError(
                FilesErrorCode.InvalidParameter,
                result.Message ?? "Invalid params.",
                StatusCodes.Status400BadRequest,
                result.Details);
        }

        return CreateError(
            FilesErrorCode.FileOperationFailed,
            result.Message ?? "Failed to process file request.",
            StatusCodes.Status500InternalServerError,
            result.Details);
    }

    private static IActionResult CreateError(
        FilesErrorCode code,
        string title,
        int statusCode,
        string? detail)
    {
        var result = ApiResult.Fail((int)code, title, statusCode);
        return string.IsNullOrWhiteSpace(detail)
            ? result
            : result.WithDetail(detail);
    }
}
