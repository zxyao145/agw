using DSystem.Appliaction.Services;
using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using DSystem.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/agentflows")]
public class AgentflowsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private readonly AgentflowDomainService _agentflowService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;

    public AgentflowsController(
        AgentflowDomainService agentflowService,
        AgentflowRuntimeService agentflowRuntimeService)
    {
        _agentflowService = agentflowService;
        _agentflowRuntimeService = agentflowRuntimeService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var agentflows = await _agentflowService.ListAsync();
        return Ok(agentflows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agentflow = await _agentflowService.GetAsync(id);
        return agentflow == null ? NotFound() : Ok(agentflow);
    }

    [HttpGet("{id:guid}/nodes")]
    public async Task<IActionResult> ListNodesAsync(Guid id)
    {
        var agentflow = await _agentflowService.GetAsync(id);
        if (agentflow == null)
        {
            return NotFound();
        }

        var nodes = await _agentflowService.ListNodesAsync(id);
        return Ok(nodes);
    }

    [HttpGet("{id:guid}/edges")]
    public async Task<IActionResult> ListEdgesAsync(Guid id)
    {
        var agentflow = await _agentflowService.GetAsync(id);
        if (agentflow == null)
        {
            return NotFound();
        }

        var edges = await _agentflowService.ListEdgesAsync(id);
        return Ok(edges);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] AgentflowCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var agentflow = new Agentflow
        {
            Name = request.Name,
            Description = request.Description,
            Pattern = request.Pattern,
            ConfigurationJson = request.ConfigurationJson,
            Enable = request.Enable
        };

        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var created = await _agentflowService.CreateAsync(agentflow, nodes, edges, user);
        if (created == null)
        {
            return BadRequest("Failed to create agentflow (validation failed or referenced resources not found).");
        }

        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentflowUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var updated = await _agentflowService.UpdateAsync(id, agentflow =>
        {
            agentflow.Name = request.Name;
            agentflow.Description = request.Description;
            agentflow.Pattern = request.Pattern;
            agentflow.ConfigurationJson = request.ConfigurationJson;
            agentflow.Enable = request.Enable;
        }, nodes, edges, user);

        return updated == null
            ? NotFound()
            : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentflowService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(Guid id, [FromBody] AgentflowExecuteRequest request, CancellationToken cancellationToken)
    {
        var result = await _agentflowRuntimeService.ExecuteAsync(id, request.Input, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentflowExecuteResponse.FromDomain(result));
    }

    [HttpPost("{id:guid}/execute-sse")]
    public async Task ExecuteSseAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        try
        {
            var request = await ReceiveRequestAsync<AgentflowExecuteRequest>(webSocket, cancellationToken);
            if (request == null)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
                return;
            }

            await foreach (var message in _agentflowRuntimeService.ExecuteStreamingAsync(id, request.Input, cancellationToken))
            {
                var json = JsonUtil.Serialize(message);
                await SendJsonAsync(webSocket, json, cancellationToken);
            }

            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Completed");
        }
        catch (OperationCanceledException)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Request cancelled");
        }
        catch (WebSocketException)
        {
            // Connection closed by client.
        }
    }

    private async Task<T?> ReceiveRequestAsync<T>(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Connection closed by client");
            return default;
        }

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        try { return JsonUtil.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    private static Task SendJsonAsync(WebSocket webSocket, string json, CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open) return Task.CompletedTask;
        var data = Encoding.UTF8.GetBytes(json);
        return webSocket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        if (webSocket.State != WebSocketState.Open) return Task.CompletedTask;
        return webSocket.CloseAsync(status, reason, CancellationToken.None);
    }
}
