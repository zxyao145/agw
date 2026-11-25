using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
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
    private readonly AcpRegistryAppService _acpRegistry;

    public AgentsController(
        AgentAppService agentAppService,
        AgentSuggestionAppService agentSuggestionAppService,
        AcpRegistryAppService acpRegistry
    )
    {
        _agentAppService = agentAppService;
        _agentSuggestionAppService = agentSuggestionAppService;
        _acpRegistry = acpRegistry;
    }

    [HttpGet]
    [ProducesApiResult(typeof(AgentListResponse[]))]
    public async Task<IActionResult> ListAsync()
    {
        var agents = await _agentAppService.ListAgentsForCurrentUserAsync();
        return ApiResult.Ok(agents.Select(AgentListResponse.FromDomain).ToArray());
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<AgentResponse>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var page = await _agentAppService.ListAgentPageForCurrentUserAsync(pageIndex, pageSize, cancellationToken);
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
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(id);
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

    [HttpGet("acp-registry")]
    [ProducesApiResult(typeof(AcpRegistryResponse))]
    public async Task<IActionResult> GetAcpRegistryAsync(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default
    ) => ApiResult.Ok(await _acpRegistry.GetAsync(refresh, cancellationToken));

    [HttpGet("external-options")]
    [ProducesApiResult(typeof(ExternalAgentOptionResponse[]))]
    public IActionResult GetExternalOptions()
    {
        return ApiResult.Ok(
            ExternalAgentRules
                .SupportedKinds.Select(kind => new ExternalAgentOptionResponse(
                    kind,
                    ExternalAgentDefaults.GetDisplayName(kind),
                    ExternalAgentDefaults.GetDefaultExtra(kind),
                    ExternalAgentDefaults.GetSupportedProviderTypes(kind)
                ))
                .ToArray()
        );
    }

    [HttpPost]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> CreateAsync(
        [FromBody] AgentCreateRequest request,
        CancellationToken cancellationToken = default
    )
    {
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
            Type = request.Type ?? AgentType.System,
            ExternalAgentKind = request.ExternalAgentKind ?? EngineKind.Maf,
            Extra = request.Extra,
            ResponseSchema = request.ResponseSchema,
        };

        var created = await _agentAppService.CreateAgentAsync(
            agent,
            new AgentCreateCommand
            {
                McpToolServerIds = request.McpToolServerIds,
                SkillIds = request.SkillIds,
                ConnectionIds = request.ConnectionIds,
                A2AEndpoint = request.A2AEndpoint?.ToInput(),
            },
            cancellationToken
        );
        return created == null
            ? ApiResult.BadRequest("Failed to create agent.", ErrorCodes.InvalidParam.Code)
            : ApiResult.Ok(AgentResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> UpdateAsync(
        Guid id,
        [FromBody] AgentUpdateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var updated = await _agentAppService.UpdateAgentAsync(id, request.ToCommand(), cancellationToken);

        return updated == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(AgentResponse.FromDomain(updated));
    }

    [HttpPost("a2a-card/preview")]
    [ProducesApiResult(typeof(A2ACardPreviewResponse))]
    public async Task<IActionResult> PreviewA2ACardAsync(
        [FromBody] A2ACardPreviewRequest request,
        CancellationToken cancellationToken = default
    ) => ApiResult.Ok(await _agentAppService.PreviewA2ACardAsync(request, cancellationToken));

    [HttpPost("a2a-card/refresh")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> RefreshA2ACardAsync(
        [FromBody] A2ACardRefreshRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var agent = await _agentAppService.RefreshA2ACardAsync(request.AgentId, cancellationToken);
        return agent is null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(AgentResponse.FromDomain(agent));
    }

    [HttpPut("enabled")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> UpdateEnabledAsync(
        [FromBody] AgentEnabledUpdateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var updated = await _agentAppService.UpdateAgentEnabledAsync(
            request.AgentId,
            request.Enable,
            cancellationToken
        );
        return updated == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(AgentResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _agentAppService.DeleteAgentAsync(id, cancellationToken);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
