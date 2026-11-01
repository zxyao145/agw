using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Bens.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Agw.Files.Api;

[ApiController]
[Route("api/files/repositories")]
public sealed class RepositoryWorkspaceController : ControllerBase
{
    private readonly RepositoryWorkspaceAppService _service;

    public RepositoryWorkspaceController(RepositoryWorkspaceAppService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadAsync([FromQuery, BindRequired] Guid projectId, CancellationToken ct) =>
        ApiResult.Ok(await _service.ReadAsync(projectId, ct));

    [HttpPost("clone")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CloneAsync([FromBody] RepositoryWorkspaceRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.ExecuteAsync("clone", request, ct));

    [HttpPost("feature")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateFeatureAsync(
        [FromBody] RepositoryWorkspaceRequest request,
        CancellationToken ct
    ) => ApiResult.Ok(await _service.ExecuteAsync("feature", request, ct));

    [HttpPost("switch")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SwitchAsync([FromBody] RepositoryWorkspaceRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.ExecuteAsync("switch", request, ct));

    [HttpPost("merge")]
    [ProducesResponseType(typeof(ApiResult<RepositoryWorkspaceState>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MergeAsync([FromBody] RepositoryWorkspaceRequest request, CancellationToken ct) =>
        ApiResult.Ok(await _service.ExecuteAsync("merge", request, ct));
}
