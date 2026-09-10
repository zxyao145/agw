using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agents/permission-capabilities")]
public sealed class ExecutionPermissionsController : ControllerBase
{
    private readonly ExecutionPermissionService _permissions;

    public ExecutionPermissionsController(ExecutionPermissionService permissions)
    {
        _permissions = permissions;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ExecutionPermissionCapabilities))]
    public async Task<IActionResult> GetAsync(
        [FromQuery] AgentRuntimeType type,
        [FromQuery] Guid id,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await _permissions.GetAsync(type, id, cancellationToken));
}
