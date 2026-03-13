using DSystem.Appliaction.Services;
using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using DSystem.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
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
            DisplayName = request.DisplayName,
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            ModelProviderApiKeyId = request.ModelProviderApiKeyId,
            Tools = request.Tools
        };

        var created = await _agentService.CreateAsync(agent, request.McpToolServerIds, user);
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
            agent.DisplayName = request.DisplayName;
            agent.Description = request.Description;
            agent.SystemPrompt = request.SystemPrompt;
            agent.ModelProviderApiKeyId = request.ModelProviderApiKeyId;
            agent.Tools = request.Tools;
        }, request.McpToolServerIds, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
