using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/traces")]
public class TracesController : ControllerBase
{
    private readonly AgentflowTraceAppService _appService;

    public TracesController(AgentflowTraceAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(PagedResult<AgentflowTraceDto>))]
    public async Task<IActionResult> ListAsync(
        [FromQuery] Guid? projectId,
        [FromQuery] string? contextId,
        [FromQuery] Guid? agentflowId,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new AgentflowTraceQuery
        {
            ProjectId = projectId,
            ContextId = contextId,
            AgentflowId = agentflowId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };

        var result = await _appService.ListAsync(query, cancellationToken);
        return AgwApiResult.Ok(result);
    }
}
