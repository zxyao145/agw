using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;
using Agw.Shared.Exceptions;
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
        [FromQuery] bool recursive = false,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.ListAsync(
            projectId,
            path,
            diff,
            recursive,
            RequestCancellationToken,
            directoryId
        );
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
        }

        var items = result
            .Value!.Items.Select(entry => new FileItem
            {
                DirectoryId = directoryId,
                Name = entry.Name,
                Path = entry.Path,
                Type = entry.Type,
                Size = entry.Size,
                ModifiedTime = entry.ModifiedTime,
                GitStatus = entry.GitStatus,
                GitStagedStatus = entry.GitStagedStatus,
                GitUnstagedStatus = entry.GitUnstagedStatus,
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
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.ReadAsync(projectId, path, RequestCancellationToken, directoryId);
        return result.Status == FileOperationStatus.Success ? ApiResult.Ok(result.Value) : MapError(result);
    }

    [HttpGet("diff")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiffAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] string? scope = null,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.DiffAsync(projectId, path, scope, RequestCancellationToken, directoryId);
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
        }

        if (result.Value!.Unchanged)
        {
            return ApiResult.Ok(
                new
                {
                    diff = "",
                    message = "No changes detected",
                    unchanged = true,
                    originalContent = result.Value.OriginalContent,
                }
            );
        }

        return ApiResult.Ok(new { diff = result.Value.Diff, unchanged = false });
    }

    [HttpDelete("delete")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.DeleteAsync(projectId, path, RequestCancellationToken, directoryId);
        return MapMutationResult(result);
    }

    [HttpPost("reset")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResetAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.ResetAsync(projectId, path, RequestCancellationToken, directoryId);
        return MapMutationResult(result);
    }

    [HttpPost("stage")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> StageAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        return await SetStagedAsync(projectId, path, staged: true, directoryId);
    }

    [HttpPost("unstage")]
    [ProducesResponseType(typeof(ApiResult<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UnstageAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        return await SetStagedAsync(projectId, path, staged: false, directoryId);
    }

    private async Task<IActionResult> SetStagedAsync(Guid projectId, string? path, bool staged, Guid? directoryId)
    {
        var result = staged
            ? await _fileAppService.StageAsync(projectId, path, RequestCancellationToken, directoryId)
            : await _fileAppService.UnstageAsync(projectId, path, RequestCancellationToken, directoryId);
        return MapMutationResult(result);
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
        [FromQuery] bool recursive = true,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.SearchAsync(
            projectId,
            path,
            keyword,
            limit,
            recursive,
            RequestCancellationToken,
            directoryId
        );
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
        }

        var results = result
            .Value!.Results.Select(entry => new FileSearchResult
            {
                DirectoryId = directoryId,
                FullPath = entry.FullPath,
                RelativePath = entry.RelativePath,
                Type = entry.Type,
            })
            .ToList();
        return ApiResult.Ok(new FileSearchResponse { Results = results });
    }

    private CancellationToken RequestCancellationToken =>
        ControllerContext.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static IActionResult MapError<T>(FileOperationResult<T> result)
    {
        var errorCode = result.Status switch
        {
            FileOperationStatus.NotFound => ErrorCodes.ResourceNotFound,
            FileOperationStatus.InvalidRequest => ErrorCodes.InvalidParam,
            _ => ErrorCodes.FileOperationFailed,
        };
        return CreateError(errorCode, result.Message ?? errorCode.Message, result.Details);
    }

    private IActionResult MapMutationResult(FileOperationResult<FileMutationOutput> result)
    {
        if (result.Status != FileOperationStatus.Success)
        {
            return MapError(result);
        }

        return ApiResult.Ok(new { success = result.Value!.Success, message = result.Value.Message });
    }

    private static IActionResult CreateError(ErrorCode errorCode, string title, string? detail)
    {
        var result = ApiResult.Fail(errorCode.Code, title, (int)errorCode.StatusCode);
        return string.IsNullOrWhiteSpace(detail) ? result : result.WithDetail(detail);
    }
}
