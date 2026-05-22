using Agw.Agents.Application.Agents;
using Agw.Agents.Contracts.Manager;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Controllers.Manager;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentAppService _agentAppService;

    public AgentsController(AgentAppService agentAppService)
    {
        _agentAppService = agentAppService;
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
            Tools = request.Tools
        };

        var created = await _agentAppService.CreateAgentAsync(
            agent,
            request.McpToolServerIds,
            request.SkillIds,
            request.AppInstanceIds,
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
                agent.Tools = request.Tools;
            },
            request.McpToolServerIds,
            request.SkillIds,
            request.AppInstanceIds,
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
