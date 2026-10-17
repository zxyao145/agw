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
    /// <summary>
    /// 写入请求的上限：文件内容上限加 1 MiB 的 multipart 开销，内容本身是否超限由 <see cref="FileAppService"/> 判断。
    /// The write request limit: the content limit plus 1 MiB of multipart overhead; whether the content itself exceeds
    /// the limit is decided by <see cref="FileAppService"/>.
    /// </summary>
    private const long MaxWriteRequestBytes = FileAppService.MaxTransferBytes + 1024 * 1024;

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

        var items = result.Value!.Items.Select(entry => ToFileItem(entry, directoryId)).ToList();
        return ApiResult.Ok(new FileListResponse { Items = items });
    }

    [HttpGet("stat")]
    [ProducesResponseType(typeof(ApiResult<FileItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StatAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path = "",
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.StatAsync(projectId, path, RequestCancellationToken, directoryId);
        return MapEntryResult(result, directoryId);
    }

    /// <summary>
    /// 成功时响应体是文件内容（application/octet-stream，带 Content-Length）；失败时与其他接口一样返回 ApiResult。
    /// On success the body is the file content (application/octet-stream with Content-Length); failures return an
    /// ApiResult like every other endpoint.
    /// </summary>
    [HttpGet("download")]
    [ProducesResponseType(typeof(Stream), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status413RequestEntityTooLarge)]
    public async Task<IActionResult> DownloadAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.OpenReadAsync(projectId, path, RequestCancellationToken, directoryId);
        return result.Status == FileOperationStatus.Success
            ? File(result.Value!, "application/octet-stream")
            : MapError(result);
    }

    [HttpPut("write")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxWriteRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxWriteRequestBytes)]
    [ProducesResponseType(typeof(ApiResult<FileItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status413RequestEntityTooLarge)]
    public async Task<IActionResult> WriteAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        IFormFile content,
        [FromQuery] bool overwrite = false,
        [FromQuery] bool createParents = false,
        [FromQuery] Guid? directoryId = null
    )
    {
        await using var stream = content.OpenReadStream();
        var result = await _fileAppService.WriteAsync(
            projectId,
            path,
            stream,
            overwrite,
            createParents,
            RequestCancellationToken,
            directoryId
        );
        return MapEntryResult(result, directoryId);
    }

    [HttpPost("create-directory")]
    [ProducesResponseType(typeof(ApiResult<FileItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateDirectoryAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] bool recursive = false,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.CreateDirectoryAsync(
            projectId,
            path,
            recursive,
            RequestCancellationToken,
            directoryId
        );
        return MapEntryResult(result, directoryId);
    }

    [HttpPost("move")]
    [ProducesResponseType(typeof(ApiResult<FileItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? sourcePath,
        [FromQuery] string? destinationPath,
        [FromQuery] bool overwrite = false,
        [FromQuery] Guid? directoryId = null
    )
    {
        var result = await _fileAppService.MoveAsync(
            projectId,
            sourcePath,
            destinationPath,
            overwrite,
            RequestCancellationToken,
            directoryId
        );
        return MapEntryResult(result, directoryId);
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
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery] string? path,
        [FromQuery] Guid? directoryId = null,
        [FromQuery] bool recursive = true
    )
    {
        var result = await _fileAppService.DeleteAsync(
            projectId,
            path,
            recursive,
            RequestCancellationToken,
            directoryId
        );
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

    private static IActionResult MapEntryResult(FileOperationResult<FileListEntry> result, Guid? directoryId) =>
        result.Status == FileOperationStatus.Success
            ? ApiResult.Ok(ToFileItem(result.Value!, directoryId))
            : MapError(result);

    private static FileItem ToFileItem(FileListEntry entry, Guid? directoryId) =>
        new()
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
        };

    private static IActionResult CreateError(ErrorCode errorCode, string title, string? detail)
    {
        var result = ApiResult.Fail(errorCode.Code, title, (int)errorCode.StatusCode);
        return string.IsNullOrWhiteSpace(detail) ? result : result.WithDetail(detail);
    }
}
