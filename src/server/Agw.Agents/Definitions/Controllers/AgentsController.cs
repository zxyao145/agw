using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentAppService _agentAppService;
    private readonly AgentSuggestionAppService _agentSuggestionAppService;

    public AgentsController(AgentAppService agentAppService, AgentSuggestionAppService agentSuggestionAppService)
    {
        _agentAppService = agentAppService;
        _agentSuggestionAppService = agentSuggestionAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(AgentResponse[]))]
    public async Task<IActionResult> ListAsync()
    {
        var agents = await _agentAppService.ListAgentsAsync();
        return ApiResult.Ok(agents.Select(AgentResponse.FromDomain).ToArray());
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<AgentResponse>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var page = await _agentAppService.ListAgentPageAsync(pageIndex, pageSize, cancellationToken);
        return ApiResult.Ok(
            new PagedResult<AgentResponse>
            {
                Items = page.Items.Select(AgentResponse.FromDomain).ToList(),
                Total = page.Total,
                PageIndex = page.PageIndex,
                PageSize = page.PageSize,
            }
        );
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agent = await _agentAppService.GetAgentAsync(id);
        return agent == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(AgentResponse.FromDomain(agent));
    }

    [HttpGet("suggestions")]
    [ProducesApiResult(typeof(AgentSuggestionsResponse))]
    public async Task<IActionResult> SuggestionsAsync([FromQuery] Guid? projectId, [FromQuery] Guid agentId)
    {
        var suggestions = await _agentSuggestionAppService.GetSuggestionsAsync(projectId, agentId);
        return ApiResult.Ok(suggestions);
    }

    [HttpPost]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] AgentCreateRequest request)
    {
        var toolsError = ToolValueObjectValidation.GetError(request.Tools);
        if (toolsError != null)
        {
            return ApiResult.BadRequest(toolsError, ErrorCodes.InvalidParam.Code);
        }

        var user = User.GetUserId();
        var agent = new Agent
        {
            DisplayName = request.DisplayName,
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            ModelProviderId = request.ModelProviderId,
            SummaryModelProviderId = request.SummaryModelProviderId,
            EnableSummary = request.EnableSummary,
            Tools = request.Tools ?? [],
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>(),
        };

        var created = await _agentAppService.CreateAgentAsync(
            agent,
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds,
            user
        );
        return created == null
            ? ApiResult.BadRequest("Failed to create agent.", ErrorCodes.InvalidParam.Code)
            : ApiResult.Ok(AgentResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
    {
        var toolsError = ToolValueObjectValidation.GetError(request.Tools);
        if (toolsError != null)
        {
            return ApiResult.BadRequest(toolsError, ErrorCodes.InvalidParam.Code);
        }

        var user = User.GetUserId();
        var updated = await _agentAppService.UpdateAgentAsync(id, request.ToCommand(), user);

        return updated == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(AgentResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentAppService.DeleteAgentAsync(id);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
