using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Api.Execution;
using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Shared.Models;
using Agw.Shared.Utils;
using ClaudeCodeSdk.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/executions")]
public partial class AgentExecutionsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
    private readonly ExecutionCommandDispatcher _commandDispatcher;
    private readonly IAgentExecutionCoordinator _agentExecutionCoordinator;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly ILogger<AgentExecutionsController> _logger;

    public AgentExecutionsController(
        ExecutionCommandDispatcher commandDispatcher,
        IAgentExecutionCoordinator agentExecutionCoordinator,
        AgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        ILogger<AgentExecutionsController> logger)
    {
        _commandDispatcher = commandDispatcher;
        _agentExecutionCoordinator = agentExecutionCoordinator;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _logger = logger;
    }

    [HttpGet("{agentId:guid}/ws")]
    public async Task ExecuteWsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var sendLock = new SemaphoreSlim(1, 1);

        var connectionState = new ExecutionConnectionState();
        var commandContext = CreateCommandContext(
            agentId,
            connectionState,
            webSocket,
            sendLock,
            cancellationToken);

        try
        {
            while (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await connectionState.ReleaseCompletedExecutionAsync();

                var command = await ReceiveRequestAsync<AgentRunCommand>(webSocket, cancellationToken);
                if (command == null)
                {
                    await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Not Support Payload");
                    break;
                }

                var result = await _commandDispatcher.DispatchAsync(command, commandContext);
                if (result.CloseConnection)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Operation Canceled");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Request cancelled");
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "Unexpected WebSocketException");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "WebSocket Error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in WebSocket handler");
            await SendErrorAsync(webSocket, $"Unexpected error: {ex.Message}", sendLock, cancellationToken);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Unexpected error");
        }
        finally
        {
            await DisposeConnectionResourcesAsync(connectionState, commandContext.AgentSession);
            _logger.LogDebug("WebSocket connection closed");
        }
    }

    private void ObserveActiveExecTask(Task inputTask)
    {
        _ = inputTask.ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                if (task.Exception != null)
                {
                    _logger.LogError(task.Exception, "Unhandled error while processing agent input");
                }
            },
            TaskScheduler.Default);
    }

    private async Task AwaitActiveExecTaskAsync(Task inputTask)
    {
        try { await inputTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while awaiting ClaudeCode input task");
        }
    }

    private ExecutionCommandContext CreateCommandContext(
        Guid agentId,
        ExecutionConnectionState connectionState,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        return new ExecutionCommandContext
        {
            AgentId = agentId,
            AgentSession = null,
            CancellationToken = cancellationToken,
            CurrentUser = User?.Identity?.Name ?? "system",
            ConnectionState = connectionState,
            ExecutionCoordinator = _agentExecutionCoordinator,
            WebSocket = webSocket,
            SendLock = sendLock,
            SendErrorAsync = errorMessage => SendErrorAsync(webSocket, errorMessage, sendLock, cancellationToken),
            SendSystemMessageAsync = message => SendSystemMessageAsync(webSocket, sendLock, message, cancellationToken),
            CloseConnectionAsync = (status, reason) => TryCloseAsync(webSocket, status, reason),
            ExtractReason = ExtractReason,
            ObserveExecution = ObserveActiveExecTask
        };
    }

    private async Task DisposeConnectionResourcesAsync(
        ExecutionConnectionState connectionState,
        AgentExecSession? agentSession)
    {
        if (connectionState.ActiveExecution != null)
        {
            connectionState.ActiveExecution.RequestInterrupt(null);
            await AwaitActiveExecTaskAsync(connectionState.ActiveExecution.ExecutionTask);
            await connectionState.ActiveExecution.DisposeAsync();
        }

        if (agentSession == null)
        {
            return;
        }

        agentSession.CancelActiveRequest();
        await agentSession.DisposeAsync();
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
        try
        {
            var request = JsonUtil.Deserialize<T>(json);
            if (request == null)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
                return default;
            }

            return request;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to deserialize WebSocket message: {Message}.", json);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
            return default;
        }
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

    private static Task SendSystemMessageAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new AgwMessage(
            Guid.NewGuid().ToString("D"),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { new AgwTextContent { Content = message } });
        return SendJsonAsync(webSocket, JsonUtil.Serialize(payload), sendLock, cancellationToken);
    }

    private static async Task SendJsonAsync(
        WebSocket webSocket,
        string json,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open) return;

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State != WebSocketState.Open) return;

            var data = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        return webSocket.State switch
        {
            WebSocketState.Open => webSocket.CloseAsync(status, reason, CancellationToken.None),
            WebSocketState.CloseReceived => webSocket.CloseOutputAsync(status, reason, CancellationToken.None),
            _ => Task.CompletedTask
        };
    }

    private Task SendErrorAsync(
        WebSocket webSocket, 
        string errorMessage,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken
        ) =>
        SendJsonAsync(webSocket, JsonUtil.Serialize(CreateErrorMessage(errorMessage)), sendLock, cancellationToken);


    private static AgwMessage CreateErrorMessage(string errorMessage)
    {
        var content = new AgwErrorContent
        {
            Content = errorMessage
        };

        var payload = new AgwMessage
            (
                Guid.NewGuid().ToString("D"),
                "$agw-server",
                AiRole.System,
                new List<AgwContent> { content }
            );
        return payload;
    }
}
