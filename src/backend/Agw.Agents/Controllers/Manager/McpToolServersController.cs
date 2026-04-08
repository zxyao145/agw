using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Manager.Api.Controllers;

[ApiController]
[Route("api/mcp-tool-servers")]
public class McpToolServersController : ControllerBase
{
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly ILogger<McpToolServersController> _logger;

    public McpToolServersController(
        AgentRuntimeService agentRuntimeService,
        ILogger<McpToolServersController> logger)
    {
        _agentRuntimeService = agentRuntimeService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var servers = await _agentRuntimeService.ListMcpToolServersAsync();
        return Ok(servers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var server = await _agentRuntimeService.GetMcpToolServerAsync(id);
        return server == null ? NotFound() : Ok(server);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] McpToolServerCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var server = new McpServer
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

        var created = await _agentRuntimeService.CreateMcpToolServerAsync(server, request.AgentIds, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] McpToolServerUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _agentRuntimeService.UpdateMcpToolServerAsync(
            id,
            server =>
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
            },
            user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentRuntimeService.DeleteMcpToolServerAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("connect")]
    public async Task<IActionResult> ConnectAsync(
        [FromBody] McpToolServerConnectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tools = await _agentRuntimeService.ListMcpToolsAsync(request.McpToolServerId, cancellationToken);
            var toolItems = tools
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new McpToolItem(x.Name))
                .ToList();

            return Ok(new McpToolServerConnectResponse("success", toolItems));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect MCP tool server {McpToolServerId}", request.McpToolServerId);
            return Ok(new McpToolServerConnectResponse("failed", []));
        }
    }
}
