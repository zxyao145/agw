using Agw.Agents.Application.Agents;
using Agw.Agents.Contracts.Manager;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Controllers.Manager;

[ApiController]
[Route("api/mcp-tool-servers")]
public class McpToolServersController : ControllerBase
{
    private readonly McpToolServerAppService _mcpToolServerAppService;
    private readonly ILogger<McpToolServersController> _logger;

    public McpToolServersController(
        McpToolServerAppService mcpToolServerAppService,
        ILogger<McpToolServersController> logger)
    {
        _mcpToolServerAppService = mcpToolServerAppService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var servers = await _mcpToolServerAppService.ListMcpToolServersAsync();
        return AgwApiResult.Ok(servers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var server = await _mcpToolServerAppService.GetMcpToolServerAsync(id);
        return server == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(server);
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

        var created = await _mcpToolServerAppService.CreateMcpToolServerAsync(server, request.AgentIds, user);
        return AgwApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] McpToolServerUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _mcpToolServerAppService.UpdateMcpToolServerAsync(
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

        return updated == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _mcpToolServerAppService.DeleteMcpToolServerAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }

    [HttpPost("connect")]
    public async Task<IActionResult> ConnectAsync(
        [FromBody] McpToolServerConnectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tools = await _mcpToolServerAppService.ListMcpToolsAsync(request.McpToolServerId, cancellationToken);
            var toolItems = tools
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new McpToolItem(x.Name))
                .ToList();

            return AgwApiResult.Ok(new McpToolServerConnectResponse("success", toolItems));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect MCP tool server {McpToolServerId}", request.McpToolServerId);
            return AgwApiResult.Ok(new McpToolServerConnectResponse("failed", []));
        }
    }
}
