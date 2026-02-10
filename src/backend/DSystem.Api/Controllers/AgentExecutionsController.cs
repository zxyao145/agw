using DSystem.Appliaction.Services;
using DSystem.Api.Contracts;
using DSystem.Shared;
using DSystem.Shared.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/executions")]
public class AgentExecutionsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;

    public AgentExecutionsController(
        AgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService)
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
    }

    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(
        Guid id,
        [FromBody] AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return request.AgentType switch
        {
            ProjectTaskAgentType.Agent => await ExecuteAgentAsync(id, request, cancellationToken),
            ProjectTaskAgentType.Agentflow => await ExecuteAgentflowAsync(id, request, cancellationToken),
            _ => BadRequest("Invalid AgentType.")
        };
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
            var request = await ReceiveRequestAsync<AgentExecutionRequest>(webSocket, cancellationToken);
            if (request == null)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
                return;
            }

            await SendStreamingMessagesAsync(id, request, webSocket, cancellationToken);

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

    private async Task<IActionResult> ExecuteAgentAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _agentRuntimeService.ExecuteAsync(
            id,
            request.ThreadId ?? string.Empty,
            request.Input,
            cancellationToken,
            request.ProjectId);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecutionResponse.FromAgentResult(result));
    }

    private async Task<IActionResult> ExecuteAgentflowAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _agentflowRuntimeService.ExecuteAsync(id, request.Input, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecutionResponse.FromAgentflowResult(result));
    }

    private async Task SendStreamingMessagesAsync(
        Guid id,
        AgentExecutionRequest request,
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        switch (request.AgentType)
        {
            case ProjectTaskAgentType.Agent:
                var session = await _agentRuntimeService.CreateSessionAsync(
                    id,
                    request.ThreadId ?? string.Empty,
                    request.ProjectId,
                    cancellationToken);
                if (session == null)
                {
                    await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Agent not found.");
                    break;
                }

                await using (session)
                {
                    await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(
                                       session,
                                       request.Input,
                                       cancellationToken))
                    {
                        var json = JsonUtil.Serialize(message);
                        await SendJsonAsync(webSocket, json, cancellationToken);
                    }
                }

                break;
            case ProjectTaskAgentType.Agentflow:
                await foreach (var message in _agentflowRuntimeService.ExecuteStreamingAsync(
                                   id,
                                   request.Input,
                                   cancellationToken))
                {
                    var json = JsonUtil.Serialize(message);
                    await SendJsonAsync(webSocket, json, cancellationToken);
                }

                break;
            default:
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid AgentType.");
                break;
        }
    }

    private async Task<T?> ReceiveRequestAsync<T>(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        using var stream = new MemoryStream();
        WebSocketReceiveResult? result;

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
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "Invalid request payload");
                return default;
            }

            if (stream.Length + result.Count > MaxRequestBytes)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "Request payload too large");
                return default;
            }

            stream.Write(buffer, 0, result.Count);
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
