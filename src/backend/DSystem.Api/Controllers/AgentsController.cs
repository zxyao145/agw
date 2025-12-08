using System;
using System.Threading.Tasks;
using DSystem.Api.Contracts;
using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentDomainService _agentService;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly ModelProviderApiKeyDomainService _apiKeyService;

    public AgentsController(
        AgentDomainService agentService,
        AgentRuntimeService agentRuntimeService,
        ModelProviderApiKeyDomainService apiKeyService)
    {
        _agentService = agentService;
        _agentRuntimeService = agentRuntimeService;
        _apiKeyService = apiKeyService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var agents = await _agentService.ListAsync();
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agent = await _agentService.GetAsync(id);
        return agent == null ? NotFound() : Ok(agent);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] AgentCreateRequest request)
    {
        var apiKey = await _apiKeyService.GetAsync(request.ModelProviderApiKeyId);
        if (apiKey == null || !apiKey.Enable)
        {
            return BadRequest("Invalid or disabled ModelProviderApiKey.");
        }

        var user = User?.Identity?.Name ?? "system";
        var agent = new Agent
        {
            Name = request.Name,
            Instructions = request.Instructions,
            SystemPrompt = request.SystemPrompt,
            ModelProviderApiKeyId = request.ModelProviderApiKeyId
        };

        var created = await _agentService.CreateAsync(agent, user);
        if (created == null)
        {
            return BadRequest("Failed to create agent.");
        }

        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
    {
        var apiKey = await _apiKeyService.GetAsync(request.ModelProviderApiKeyId);
        if (apiKey == null || !apiKey.Enable)
        {
            return BadRequest("Invalid or disabled ModelProviderApiKey.");
        }

        var user = User?.Identity?.Name ?? "system";
        var updated = await _agentService.UpdateAsync(id, agent =>
        {
            agent.Name = request.Name;
            agent.Instructions = request.Instructions;
            agent.SystemPrompt = request.SystemPrompt;
            agent.ModelProviderApiKeyId = request.ModelProviderApiKeyId;
        }, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
