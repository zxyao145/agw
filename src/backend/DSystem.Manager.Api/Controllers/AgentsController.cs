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

    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(Guid id, [FromBody] AgentExecuteRequest request, CancellationToken cancellationToken)
    {
        var result = await _agentRuntimeService.ExecuteAsync(
            id,
            request.ThreadId ?? "",
            request.Input,
            cancellationToken,
            request.ProjectId);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecuteResponse.FromDomain(result));
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
            var request = await ReceiveRequestAsync<AgentExecuteRequest>(webSocket, cancellationToken);
            if (request == null)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
                return;
            }

            await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(
                id,
                request.ThreadId ?? "",
                request.Input,
                cancellationToken,
                request.ProjectId))
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
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Connection closed by client");
                return default;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return default;
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxRequestBytes)
            {
                return default;
            }
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(stream.ToArray());
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
