using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Infrastructure;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DSystem.Manager.Api.Controllers;

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
        var apiKey = await _apiKeyService.GetAsync(request.ModelProviderApiKeyId);
        if (apiKey == null || !apiKey.Enable)
        {
            return BadRequest("Invalid or disabled ModelProviderApiKey.");
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

    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(Guid id, [FromBody] AgentExecuteRequest request, CancellationToken cancellationToken)
    {
        var result = await _agentRuntimeService.ExecuteAsync(id, "", request.Input, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecuteResponse.FromDomain(result));
    }


    [HttpPost("{id:guid}/execute-sse")]
    public async Task ExecuteSseAsync(Guid id, [FromBody] AgentExecuteRequest request, CancellationToken cancellationToken)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(id, request.ThreadId ?? "", request.Input, cancellationToken))
        {
            var json = JsonUtil.Serialize(message);
            var data = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            await Response.Body.WriteAsync(data, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
