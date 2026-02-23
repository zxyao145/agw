using DSystem.Appliaction;
using DSystem.Appliaction.ExternalAgents;
using DSystem.Shared;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/external-agents/claude-code")]
public class ClaudeCodeController(
    ClaudeCodeService claudeCodeService,
    ILogger<ClaudeCodeController> logger) : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Execute ClaudeCode query with WebSocket streaming (persistent connection).
    /// </summary>
    [HttpGet("ws")]
    public async Task ExecuteWebSocketAsync()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        AgentExecSession? session = null;
        Task? activeInputTask = null;

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var (request, closeReceived) = await ReceiveRequestAsync(webSocket);
                if (closeReceived) break;
                if (request == null) continue;

                (session, activeInputTask) = await ProcessRequestAsync(webSocket, session, request, activeInputTask);
            }
        }
        catch (OperationCanceledException)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Request cancelled");
        }
        catch (WebSocketException)
        {
            // Connection already closed, no action needed
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in WebSocket handler");
            await SendErrorAsync(webSocket, $"Unexpected error: {ex.Message}");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Unexpected error");
        }
        finally
        {
            if (session != null) session.CancelActiveRequest();
            if (activeInputTask != null)
            {
                await AwaitInputTaskAsync(activeInputTask);
            }
            if (session != null) await session.DisposeAsync();
            _sendLock.Dispose();
            logger.LogDebug("ClaudeCode WebSocket connection closed");
        }
    }

    private async Task<(ClaudeCodeWsRequest? request, bool closeReceived)> ReceiveRequestAsync(WebSocket webSocket)
    {
        var buffer = new byte[BufferSize];
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), HttpContext.RequestAborted);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by client", HttpContext.RequestAborted);
            return (null, true);
        }

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var request = TryParseRequest(json);
        return (request, false);
    }

    private static ClaudeCodeWsRequest? TryParseRequest(string json)
    {
        try { return JsonUtil.Deserialize<ClaudeCodeWsRequest>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<(AgentExecSession? session, Task? activeInputTask)> ProcessRequestAsync(
        WebSocket webSocket,
        AgentExecSession? currentSession,
        ClaudeCodeWsRequest request,
        Task? activeInputTask)
    {
        if (request.Type == ClaudeCodeMessageType.Setting)
        {
            if (activeInputTask is { IsCompleted: false })
            {
                currentSession?.CancelActiveRequest();
                await AwaitInputTaskAsync(activeInputTask);
            }
            var session = await HandleSettingRequestAsync(webSocket, request.Setting, currentSession);
            return (session, null);
        }

        if (request.Type == ClaudeCodeMessageType.Input)
        {
            if (activeInputTask is { IsCompleted: false })
            {
                await SendErrorAsync(webSocket, "A request is already in progress. Wait for it to complete before starting a new one.");
                return (currentSession, activeInputTask);
            }

            var inputTask = HandleInputRequestAsync(webSocket, currentSession, request.Input);
            ObserveInputTask(inputTask);
            return (currentSession, inputTask);
        }

        if (request.Type == ClaudeCodeMessageType.Interrupt)
        {
            var updatedActiveTask = await HandleInterruptRequestAsync(
                webSocket,
                currentSession,
                request.Interrupt,
                activeInputTask);
            return (currentSession, updatedActiveTask);
        }

        return (currentSession, activeInputTask);
    }

    private async Task<AgentExecSession> HandleSettingRequestAsync(
        WebSocket webSocket,
        ClaudeCodeSettingRequest? setting,
        AgentExecSession? currentSession)
    {
        if (setting == null)
        {
            await SendErrorAsync(webSocket, "Invalid init request: Setting data is required");
            return currentSession!;
        }

        if (currentSession != null) await currentSession.DisposeAsync();

        try
        {
            var session = await claudeCodeService.InitializeSessionAsync(setting, HttpContext.RequestAborted);
            logger.LogInformation("ClaudeCode session initialized: {SessionId}", setting.SessionId);
            await SendMessageAsync(webSocket, "Session initialized successfully");
            return session;
        }
        catch (Exception ex)
        {
            await SendErrorAsync(webSocket, $"Initialization error: {ex.Message}");
            return currentSession!;
        }
    }

    private async Task HandleInputRequestAsync(
        WebSocket webSocket,
        AgentExecSession? session,
        ClaudeCodeInputRequest? input)
    {
        if (session == null)
        {
            await SendErrorAsync(webSocket, "No active session. Please initialize first.");
            return;
        }

        if (input?.Input is null or { Length: 0 })
        {
            await SendErrorAsync(webSocket, "Invalid input request: Input is required");
            return;
        }

        await ProcessInputAsync(webSocket, session, input.Input);
    }

    private async Task<Task?> HandleInterruptRequestAsync(
        WebSocket webSocket,
        AgentExecSession? session,
        ClaudeCodeInterruptRequest? interrupt,
        Task? activeInputTask)
    {
        if (session == null)
        {
            await SendErrorAsync(webSocket, "No active session. Please initialize first.");
            return activeInputTask;
        }

        if (activeInputTask is { IsCompleted: false })
        {
            session.CancelActiveRequest();
            await SendMessageAsync(webSocket, "Interrupt requested. Draining buffered output.");
            await AwaitInputTaskAsync(activeInputTask);
            return null;
        }

        var reason = string.IsNullOrWhiteSpace(interrupt?.Reason)
            ? "No active request is currently running."
            : interrupt.Reason;
        await SendMessageAsync(webSocket, reason);
        return activeInputTask;
    }

    private async Task ProcessInputAsync(WebSocket webSocket, AgentExecSession session, string input)
    {
        try
        {
            session.ResetCancellationToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                HttpContext.RequestAborted,
                session.CancellationToken);
            await foreach (var message in session.ExecuteStreamingAsync(
                input, linkedCts.Token))
            {
                if (webSocket.State != WebSocketState.Open) break;
                await SendJsonAsync(webSocket, JsonUtil.Serialize(message));
            }
        }
        catch (OperationCanceledException e)
        {
            logger.LogWarning(e, "OperationCanceled");
            await SendMessageAsync(webSocket, "Request interrupted.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "some thing error");
            await SendErrorAsync(webSocket, $"Execution error: {ex.Message}");
        }
    }

    private void ObserveInputTask(Task inputTask)
    {
        _ = inputTask.ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                if (task.Exception != null)
                {
                    logger.LogError(task.Exception, "Unhandled error while processing ClaudeCode input");
                }
            },
            TaskScheduler.Default);
    }

    private Task SendMessageAsync(WebSocket webSocket, string message) =>
        SendJsonAsync(webSocket, CreateSystemMessage(message).ToAiMessage()!.Serialize());

    private Task SendErrorAsync(WebSocket webSocket, string errorMessage) =>
        SendJsonAsync(webSocket, CreateErrorMessage(errorMessage).ToAiMessage()!.Serialize());

    private async Task SendJsonAsync(WebSocket webSocket, string json)
    {
        if (webSocket.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync();
        try
        {
            if (webSocket.State != WebSocketState.Open) return;

            var data = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task AwaitInputTaskAsync(Task inputTask)
    {
        try { await inputTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while awaiting ClaudeCode input task");
        }
    }

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        if (webSocket.State != WebSocketState.Open) return Task.CompletedTask;
        return webSocket.CloseAsync(status, reason, CancellationToken.None);
    }

    private static AgentResponseUpdate CreateSystemMessage(string message)
    {
        var d = new Dictionary<string, object?>() 
        {
            { "subtype", "hint" }
        };
        return new()
        {
            Role = ChatRole.System,
            AuthorName = "d-system",
            AdditionalProperties = new AdditionalPropertiesDictionary(d),
            Contents = [new TextContent(message)]
        };
    }

    private static AgentResponseUpdate CreateErrorMessage(string errorMessage) => new()
    {
        Role = ChatRole.System,
        AuthorName = "d-system",
        Contents = [new ErrorContent(errorMessage)]
    };
}
