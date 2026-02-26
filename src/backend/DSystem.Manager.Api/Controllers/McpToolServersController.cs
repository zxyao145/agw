using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/mcp-tool-servers")]
public class McpToolServersController : ControllerBase
{
    private readonly McpToolServerDomainService _service;

    public McpToolServersController(McpToolServerDomainService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var servers = await _service.ListAsync();
        return Ok(servers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var server = await _service.GetAsync(id);
        return server == null ? NotFound() : Ok(server);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] McpToolServerCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var server = new McpToolServer
        {
            Name = request.Name,
            Description = request.Description,
            TransportType = request.TransportType,
            Command = request.Command,
            Arguments = request.Arguments ?? [],
            WorkingDirectory = request.WorkingDirectory,
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>(),
            Url = request.Url,
            Headers = request.Headers ?? new Dictionary<string, string>(),
            Enabled = request.Enabled
        };

        var created = await _service.CreateAsync(server, request.AgentIds, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] McpToolServerUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, server =>
        {
            server.Name = request.Name;
            server.Description = request.Description;
            server.TransportType = request.TransportType;
            server.Command = request.Command;
            server.Arguments = request.Arguments ?? [];
            server.WorkingDirectory = request.WorkingDirectory;
            server.EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>();
            server.Url = request.Url;
            server.Headers = request.Headers ?? new Dictionary<string, string>();
            server.Enabled = request.Enabled;
        }, request.AgentIds, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
