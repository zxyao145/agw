using Agw.Files.Abstracts;
using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;
using Bens.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Agw.Files.Api;

[ApiController]
[Route("api/files/repositories")]
[ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
public sealed class RepositoryWorkspaceController : ControllerBase
{
    private readonly RepositoryWorkspaceAppService _service;

    public RepositoryWorkspaceController(RepositoryWorkspaceAppService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadAsync([FromQuery, BindRequired] Guid projectId, CancellationToken ct) =>
        ApiResult.Ok(await _service.ReadAsync(projectId, ct));

    [HttpGet("tree")]
    [ProducesResponseType(typeof(ApiResult<RepositoryTree>), StatusCodes.Status200OK)]
    public async Task<IActionResult> TreeAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery, BindRequired] string repository,
        [FromQuery, BindRequired] string branch,
        [FromQuery] string? path,
        CancellationToken ct
    ) => ApiResult.Ok(await _service.ListTreeAsync(projectId, repository, branch, path, ct));

    [HttpGet("changes")]
    [ProducesResponseType(typeof(ApiResult<RepositoryChange[]>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangesAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery, BindRequired] string repository,
        [FromQuery, BindRequired] string branch,
        CancellationToken ct
    ) => ApiResult.Ok(await _service.ListChangesAsync(projectId, repository, branch, ct));

    [HttpGet("blob")]
    [ProducesResponseType(typeof(ApiResult<RepositoryBlob>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BlobAsync(
        [FromQuery, BindRequired] Guid projectId,
        [FromQuery, BindRequired] string repository,
        [FromQuery, BindRequired] string branch,
        [FromQuery, BindRequired] string path,
        CancellationToken ct
    ) => ApiResult.Ok(await _service.ReadBlobAsync(projectId, repository, branch, path, ct));

    [HttpPost("clone")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CloneAsync([FromBody] CloneRepositoryRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.CloneAsync(request.ProjectId, request.Url, request.Name, ct));

    [HttpPost("feature")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateFeatureAsync(
        [FromBody] CreateFeatureRequest request,
        CancellationToken ct
    ) =>
        ApiResult.Ok(
            await _service.CreateFeatureAsync(
                request.ProjectId,
                request.Name,
                request.Feature,
                request.Date,
                request.Repositories,
                ct
            )
        );

    [HttpPost("switch")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SwitchAsync(
        [FromBody] SwitchWorktreeBranchRequest request,
        CancellationToken ct
    ) =>
        ApiResult.Ok(
            await _service.SwitchBranchAsync(
                request.ProjectId,
                request.Feature,
                request.Repository,
                request.Branch,
                request.CreateBranch,
                ct
            )
        );

    [HttpPost("merge")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MergeAsync([FromBody] WorktreeRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.MergeAsync(request.ProjectId, request.Feature, request.Repository, ct));

    [HttpPost("sync")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SyncAsync([FromBody] RootRepositoryRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.SyncAsync(request.ProjectId, request.Repository, ct));

    [HttpPost("restore")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreAsync([FromBody] RootRepositoryRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.RestoreDefaultBranchAsync(request.ProjectId, request.Repository, ct));
}
