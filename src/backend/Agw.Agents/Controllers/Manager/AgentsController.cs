using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Manager.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentRuntimeService _agentRuntimeService;

    public AgentsController(AgentRuntimeService agentRuntimeService)
    {
        _agentRuntimeService = agentRuntimeService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var agents = await _agentRuntimeService.ListAgentsAsync();
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agent = await _agentRuntimeService.GetAgentAsync(id);
        return agent == null ? NotFound() : Ok(agent);
    }

    [HttpPost]
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

        var created = await _agentRuntimeService.CreateAgentAsync(
            agent,
            request.McpToolServerIds,
            request.SkillIds,
            user);
        return created == null ? BadRequest("Failed to create agent.") : Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _agentRuntimeService.UpdateAgentAsync(
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
            user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentRuntimeService.DeleteAgentAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
