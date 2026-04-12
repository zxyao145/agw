using Agw.Agents.Application.Agents;
using Agw.Agents.Contracts.Manager;

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
    public async Task<ActionResult<IReadOnlyList<AgentResponse>>> ListAsync()
    {
        var agents = await _agentAppService.ListAgentsAsync();
        return Ok(agents.Select(AgentResponse.FromDomain).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentResponse>> GetAsync(Guid id)
    {
        var agent = await _agentAppService.GetAgentAsync(id);
        return agent == null ? NotFound() : Ok(AgentResponse.FromDomain(agent));
    }

    [HttpPost]
    public async Task<ActionResult<AgentResponse>> CreateAsync([FromBody] AgentCreateRequest request)
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
        return created == null ? BadRequest("Failed to create agent.") : Ok(AgentResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AgentResponse>> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
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

        return updated == null ? NotFound() : Ok(AgentResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentAppService.DeleteAgentAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
