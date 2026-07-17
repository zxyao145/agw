using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Definitions.Controllers;

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
    [ProducesApiResult(typeof(McpServer[]))]
    public async Task<IActionResult> ListAsync()
    {
        var servers = await _mcpToolServerAppService.ListMcpToolServersAsync();
        return ApiResult.Ok(servers);
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<McpServer>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var page = await _mcpToolServerAppService.ListMcpToolServerPageAsync(
            pageIndex,
            pageSize,
            cancellationToken);
        return ApiResult.Ok(page);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(McpServer))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var server = await _mcpToolServerAppService.GetMcpToolServerAsync(id);
        return server == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(server);
    }

    [HttpPost]
    [ProducesApiResult(typeof(McpServer))]
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
        return ApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(McpServer))]
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

        return updated == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _mcpToolServerAppService.DeleteMcpToolServerAsync(id);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpPost("connect")]
    [ProducesApiResult(typeof(McpToolServerConnectResponse))]
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

            return ApiResult.Ok(new McpToolServerConnectResponse("success", toolItems));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect MCP tool server {McpToolServerId}", request.McpToolServerId);
            return ApiResult.Ok(new McpToolServerConnectResponse("failed", []));
        }
    }
}
