using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agentflows")]
public class AgentflowsController : ControllerBase
{
    private readonly AgentflowAppService _agentflowAppService;
    private readonly IAgentflowMermaidProvider _mermaidProvider;

    public AgentflowsController(AgentflowAppService agentflowAppService, IAgentflowMermaidProvider mermaidProvider)
    {
        _agentflowAppService = agentflowAppService;
        _mermaidProvider = mermaidProvider;
    }

    [HttpGet]
    [ProducesApiResult(typeof(AgentflowDto[]))]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken = default)
    {
        var agentflows = await _agentflowAppService.ListAsync(cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(agentflows.Select(MapAgentflow).ToArray());
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<AgentflowDto>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var page = await _agentflowAppService
            .ListPageAsync(pageIndex, pageSize, cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok(
            new PagedResult<AgentflowDto>
            {
                Items = page.Items.Select(MapAgentflow).ToArray(),
                Total = page.Total,
                PageIndex = page.PageIndex,
                PageSize = page.PageSize,
            }
        );
    }

    [HttpGet("detail")]
    [ProducesApiResult(typeof(AgentflowDetailResponse))]
    public async Task<IActionResult> GetDetailAsync(
        [FromQuery] Guid id,
        [FromQuery] int revision,
        CancellationToken cancellationToken = default
    )
    {
        var detail = await _agentflowAppService.GetDetailAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(
            new AgentflowDetailResponse(
                MapAgentflow(detail.Agentflow),
                detail.Revision,
                AgentflowDefinitionMapper.ToDto(detail.Definition)
            )
        );
    }

    [HttpGet("revisions")]
    [ProducesApiResult(typeof(AgentflowRevisionDto[]))]
    public async Task<IActionResult> ListRevisionsAsync(
        [FromQuery] Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var revisions = await _agentflowAppService.ListRevisionsAsync(id, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(
            revisions.Select(revision => new AgentflowRevisionDto(revision.Revision, revision.CreateTime)).ToArray()
        );
    }

    [HttpPost]
    [ProducesApiResult(typeof(AgentflowDto))]
    public async Task<IActionResult> CreateAsync(
        [FromBody] AgentflowCreateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var definition = AgentflowDefinitionMapper.ToDefinition(request.Definition);
        var created = await _agentflowAppService
            .CreateAsync(request.Name, request.Description, definition, cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok(MapAgentflow(created));
    }

    [HttpPost("revisions")]
    [ProducesApiResult(typeof(AgentflowDto))]
    public async Task<IActionResult> SaveRevisionAsync(
        [FromBody] AgentflowSaveRevisionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var definition = AgentflowDefinitionMapper.ToDefinition(request.Definition);
        var saved = await _agentflowAppService
            .SaveRevisionAsync(
                request.AgentflowId,
                request.BaseRevision,
                request.Name,
                request.Description,
                definition,
                cancellationToken
            )
            .ConfigureAwait(false);
        return ApiResult.Ok(MapAgentflow(saved));
    }

    [HttpPut("published")]
    [ProducesApiResult(typeof(AgentflowDto))]
    public async Task<IActionResult> PublishAsync(
        [FromBody] AgentflowPublishRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var published = await _agentflowAppService
            .PublishAsync(request.AgentflowId, request.Revision, cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok(MapAgentflow(published));
    }

    [HttpPost("validate")]
    [ProducesApiResult(typeof(AgentflowValidateResponse))]
    public async Task<IActionResult> ValidateAsync(
        [FromBody] AgentflowValidateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var definition = AgentflowDefinitionMapper.ToDefinition(request.Definition);
        var problems = await _agentflowAppService
            .ValidateAsync(request.AgentflowId, request.Name, definition, cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok(new AgentflowValidateResponse(problems));
    }

    [HttpPut("enabled")]
    [ProducesApiResult(typeof(AgentflowDto))]
    public async Task<IActionResult> UpdateEnabledAsync(
        [FromBody] AgentflowEnabledUpdateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var updated = await _agentflowAppService
            .UpdateEnabledAsync(request.AgentflowId, request.Enable, cancellationToken)
            .ConfigureAwait(false);
        return updated == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(MapAgentflow(updated));
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync([FromQuery] Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _agentflowAppService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpGet("mermaid")]
    [ProducesApiResult(typeof(string))]
    public async Task<IActionResult> GetMermaidAsync(
        [FromQuery] Guid id,
        [FromQuery] int revision,
        CancellationToken cancellationToken = default
    )
    {
        var text = await _mermaidProvider.GetMermaidAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return text == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(text);
    }

    private static AgentflowDto MapAgentflow(Agentflow agentflow) =>
        new(
            agentflow.Id,
            agentflow.Name,
            agentflow.Description,
            agentflow.Enable,
            agentflow.LatestRevision,
            agentflow.PublishedRevision,
            agentflow.CreateTime,
            agentflow.UpdateTime ?? agentflow.CreateTime
        );
}
