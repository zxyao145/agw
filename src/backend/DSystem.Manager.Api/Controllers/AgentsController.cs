using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly AgentDomainService _agentService;
    private readonly ModelProviderApiKeyDomainService _apiKeyService;

    public AgentsController(
        AgentDomainService agentService,
        ModelProviderApiKeyDomainService apiKeyService)
    {
        _agentService = agentService;
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
        // Validate ModelProviderApiKeyId if provided
        if (request.ModelProviderApiKeyId.HasValue)
        {
            var apiKey = await _apiKeyService.GetAsync(request.ModelProviderApiKeyId.Value);
            if (apiKey == null || !apiKey.Enable)
            {
                return BadRequest("Invalid or disabled ModelProviderApiKey.");
            }
        }

        var user = User?.Identity?.Name ?? "system";
        var agent = new Agent
        {
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            ModelProviderApiKeyId = request.ModelProviderApiKeyId,
            Tools = request.Tools
        };

        var created = await _agentService.CreateAsync(agent, user);
        if (created == null)
        {
            return BadRequest("Failed to create agent.");
        }

        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentUpdateRequest request)
    {
        // Validate ModelProviderApiKeyId if provided
        if (request.ModelProviderApiKeyId.HasValue)
        {
            var apiKey = await _apiKeyService.GetAsync(request.ModelProviderApiKeyId.Value);
            if (apiKey == null || !apiKey.Enable)
            {
                return BadRequest("Invalid or disabled ModelProviderApiKey.");
            }
        }

        var user = User?.Identity?.Name ?? "system";
        var updated = await _agentService.UpdateAsync(id, agent =>
        {
            agent.Name = request.Name;
            agent.Description = request.Description;
            agent.SystemPrompt = request.SystemPrompt;
            agent.ModelProviderApiKeyId = request.ModelProviderApiKeyId;
            agent.Tools = request.Tools;
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
