using Agw.Api.Contracts;
using Agw.Appliaction.Services;
using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/executions")]
public class AgentExecutionsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly ITaskAppService _taskAppService;

    public AgentExecutionsController(
        AgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        ITaskAppService taskAppService)
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _taskAppService = taskAppService;
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

    [HttpGet("{id:guid}/execute-ws")]
    public async Task ExecuteWsAsync(Guid id, CancellationToken cancellationToken)
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
        var (task, contextError) = await ResolveTaskAsync(request);
        if (contextError != null)
        {
            return contextError;
        }

        var result = await _agentRuntimeService.ExecuteAsync(
            id,
            request.SessionId ?? string.Empty,
            request.Input,
            cancellationToken,
            request.ProjectId,
            task?.ContextId);
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
        var (task, contextError) = await ResolveTaskAsync(request);
        if (contextError != null)
        {
            return contextError;
        }

        var result = await _agentflowRuntimeService.ExecuteAsync(
            id,
            request.SessionId ?? string.Empty,
            request.Input,
            cancellationToken,
            request.ProjectId,
            task?.ContextId);
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
        var (task, contextError) = await ResolveTaskAsync(request);
        if (contextError != null)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, ExtractReason(contextError));
            return;
        }

        switch (request.AgentType)
        {
            case ProjectTaskAgentType.Agent:
                var session = await _agentRuntimeService.CreateSessionAsync(
                    id,
                    request.SessionId ?? string.Empty,
                    request.ProjectId,
                    task?.ContextId,
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
                                   request.SessionId ?? string.Empty,
                                   request.Input,
                                   cancellationToken,
                                   request.ProjectId,
                                   task?.ContextId))
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

    private async Task<(ProjectTask? task, IActionResult? error)> ResolveTaskAsync(AgentExecutionRequest request)
    {
        if (!request.TaskId.HasValue)
        {
            return (null, null);
        }

        var task = await _taskAppService.GetTaskAsync(request.TaskId.Value);
        if (task == null)
        {
            return (null, NotFound("Task not found."));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            if (Guid.TryParse(task.ProjectId, out var projectId))
            {
                if (!string.Equals(task.ProjectId, projectId.Normalize(), StringComparison.OrdinalIgnoreCase))
                {
                    return (null, BadRequest("Task does not belong to the supplied projectId."));
                }
            }
            else
            {
                if (!string.Equals(task.ProjectId, request.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, BadRequest("Task does not belong to the supplied projectId."));
                }
            }
        }

        return (task, null);
    }

    private static string ExtractReason(IActionResult result)
    {
        return result switch
        {
            ObjectResult objectResult when objectResult.Value is string message => message,
            StatusCodeResult statusCodeResult => $"Request failed with status {statusCodeResult.StatusCode}.",
            _ => "Invalid request payload"
        };
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
