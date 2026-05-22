using System.Net.WebSockets;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Utils;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/executions")]
public partial class AgentExecutionsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
    private readonly CommandDispatcher _commandDispatcher;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly ILogger<AgentExecutionsController> _logger;
    private readonly ITaskAppService _taskAppService;

    public AgentExecutionsController(
        CommandDispatcher commandDispatcher,
        IAgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        ILogger<AgentExecutionsController> logger,
        ITaskAppService taskAppService)
    {
        _commandDispatcher = commandDispatcher;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _logger = logger;
        _taskAppService = taskAppService;
    }

    [HttpGet("{agentId:guid}/ws")]
    [ProducesResponseType(StatusCodes.Status101SwitchingProtocols)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task ExecuteWsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The socket carries both client commands and streamed execution output for a single agent run.
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        // Server writes can happen from the dispatcher and from background execution tasks, so serialize them.
        using var sendLock = new SemaphoreSlim(1, 1);

        var commandContext = new ExecutionCommandContext(agentId, User?.Identity?.Name ?? "system", cancellationToken, webSocket, sendLock)
        {
            AgentSession = null,
            CloseConnectionAsync = (status, reason) => TryCloseAsync(webSocket, status, reason),
            ObserveTurn = ObserveActiveExecTask
        };

        try
        {
            while (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // A completed turn is released before reading the next command so the connection state stays clean.
                await commandContext.ConnectionState.ReleaseCompletedExecutionAsync();

                var command = await ReceiveRequestAsync<AgentRunCommand>(webSocket, cancellationToken);
                if (command == null)
                {
                    await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Not Support Payload");
                    break;
                }

                // The dispatcher may start streaming work, interrupt an active turn, or reply immediately.
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
            await commandContext.SendErrorAsync($"Unexpected error: {ex.Message}");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Unexpected error");
        }
        finally
        {
            await DisposeConnectionResourcesAsync(commandContext.ConnectionState, commandContext.AgentSession);
            _logger.LogDebug("WebSocket connection closed");
        }
    }

    /// <summary>
    /// Observe background execution tasks so socket-driven work does not surface as unobserved exceptions.
    /// </summary>
    /// <param name="inputTask"></param>
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

    private async Task<T?> ReceiveRequestAsync<T>(WebSocket webSocket, CancellationToken cancellationToken)
        where T : AgentRunCommand
    {
        var buffer = new byte[BufferSize];
        using var stream = new MemoryStream();
        WebSocketReceiveResult? result;

        do
        {
            // WebSocket messages may arrive in multiple frames, so accumulate them until EndOfMessage.
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
                // Reject oversized requests before the payload is buffered completely.
                await TryCloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "Request payload too large");
                return default;
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(stream.ToArray());
        try
        {
            // Each inbound message is expected to be a single JSON command for the dispatcher.
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


    private async Task DisposeConnectionResourcesAsync(
        ExecutionConnectionState connectionState,
        AgentExecSession? agentSession)
    {
        // ActiveTurn is the execution task owned by the socket; finish it first so no background work keeps sending.
        if (connectionState.ActiveExecution != null)
        {
            connectionState.ActiveExecution.RequestInterrupt(null);
            await AwaitActiveExecTaskAsync(connectionState.ActiveExecution.ExecutionTask);
            await connectionState.ActiveExecution.DisposeAsync();
        }

        // Agent sessions carry model/runtime state and must be released independently of the WebSocket turn.
        if (agentSession == null)
        {
            return;
        }

        agentSession.CancelActiveRequest();
        await agentSession.DisposeAsync();
    }

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        // Use the correct close API for the current socket state and ignore repeated close attempts.
        return webSocket.State switch
        {
            WebSocketState.Open => webSocket.CloseAsync(status, reason, CancellationToken.None),
            WebSocketState.CloseReceived => webSocket.CloseOutputAsync(status, reason, CancellationToken.None),
            _ => Task.CompletedTask
        };
    }
}
