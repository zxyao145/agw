using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentAppService _agentAppService;
    private readonly AgentSuggestionAppService _agentSuggestionAppService;

    public AgentsController(
        AgentAppService agentAppService,
        AgentSuggestionAppService agentSuggestionAppService)
    {
        _agentAppService = agentAppService;
        _agentSuggestionAppService = agentSuggestionAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(AgentResponse[]))]
    public async Task<IActionResult> ListAsync()
    {
        var agents = await _agentAppService.ListAgentsAsync();
        return AgwApiResult.Ok(agents.Select(AgentResponse.FromDomain).ToArray());
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agent = await _agentAppService.GetAgentAsync(id);
        return agent == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(AgentResponse.FromDomain(agent));
    }

    [HttpGet("suggestions")]
    [ProducesApiResult(typeof(AgentSuggestionsResponse))]
    public async Task<IActionResult> SuggestionsAsync(
        [FromQuery] Guid? projectId,
        [FromQuery] Guid agentId)
    {
        var suggestions = await _agentSuggestionAppService.GetSuggestionsAsync(projectId, agentId);
        return AgwApiResult.Ok(suggestions);
    }

    [HttpPost]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] AgentCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var agent = new Agent
        {
            DisplayName = request.DisplayName,
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            ModelProviderId = request.ModelProviderId,
            SummaryModelProviderId = request.SummaryModelProviderId,
            EnableSummary = request.EnableSummary,
            Tools = request.Tools,
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>()
        };

        var created = await _agentAppService.CreateAgentAsync(
            agent,
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds,
            user);
        return created == null
            ? AgwApiResult.BadRequest("Failed to create agent.")
            : AgwApiResult.Ok(AgentResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(AgentResponse))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _agentAppService.UpdateAgentAsync(
            id,
            agent =>
            {
                agent.DisplayName = request.DisplayName;
                agent.Description = request.Description;
                agent.SystemPrompt = request.SystemPrompt;
                agent.ModelProviderId = request.ModelProviderId;
                agent.SummaryModelProviderId = request.SummaryModelProviderId;
                agent.EnableSummary = request.EnableSummary;
                agent.Tools = request.Tools;
                agent.Extra = request.Extra;
                agent.EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>();
            },
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds,
            user);

        return updated == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(AgentResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentAppService.DeleteAgentAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
