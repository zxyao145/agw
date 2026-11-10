using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agents/input-capabilities")]
public sealed class ExecutionInputController : ControllerBase
{
    private readonly ExecutionInputService _input;

    public ExecutionInputController(ExecutionInputService input)
    {
        _input = input;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ExecutionInputCapabilities))]
    public async Task<IActionResult> GetAsync(
        [FromQuery] AgentRuntimeType type,
        [FromQuery] Guid id,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await _input.GetAsync(type, id, cancellationToken));
}
