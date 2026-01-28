using DSystem.Domain.Models;
using DSystem.ExternalAgents;
using DSystem.Infrastructure;
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
        ClaudeCodeSession? session = null;

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var (request, closeReceived) = await ReceiveRequestAsync(webSocket);
                if (closeReceived) break;
                if (request == null) continue;

                session = await ProcessRequestAsync(webSocket, session, request);
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
            if (session != null) await session.DisposeAsync();
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

    private async Task<ClaudeCodeSession?> ProcessRequestAsync(
        WebSocket webSocket,
        ClaudeCodeSession? currentSession,
        ClaudeCodeWsRequest request)
    {
        if (request.Type == ClaudeCodeMessageType.Setting)
            return await HandleSettingRequestAsync(webSocket, request.Setting, currentSession);

        if (request.Type == ClaudeCodeMessageType.Input)
            await HandleInputRequestAsync(webSocket, currentSession, request.Input);

        return currentSession;
    }

    private async Task<ClaudeCodeSession> HandleSettingRequestAsync(
        WebSocket webSocket,
        ClaudeCodeSettingRequest? setting,
        ClaudeCodeSession? currentSession)
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
            logger.LogInformation("ClaudeCode session initialized: {ThreadId}", setting.SessionId);
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
        ClaudeCodeSession? session,
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

    private async Task ProcessInputAsync(WebSocket webSocket, ClaudeCodeSession session, string input)
    {
        try
        {
            await foreach (var message in claudeCodeService.ExecuteSessionStreamingAsync(
                session, input, HttpContext.RequestAborted))
            {
                if (webSocket.State != WebSocketState.Open) break;
                await SendJsonAsync(webSocket, JsonUtil.Serialize(message));
            }
        }
        catch (Exception ex)
        {
            await SendErrorAsync(webSocket, $"Execution error: {ex.Message}");
        }
    }

    private Task SendMessageAsync(WebSocket webSocket, string message) =>
        SendJsonAsync(webSocket, CreateSystemMessage(message).ToAiMessage()!.Serialize());

    private Task SendErrorAsync(WebSocket webSocket, string errorMessage) =>
        SendJsonAsync(webSocket, CreateErrorMessage(errorMessage).ToAiMessage()!.Serialize());

    private static Task SendJsonAsync(WebSocket webSocket, string json)
    {
        if (webSocket.State != WebSocketState.Open) return Task.CompletedTask;

        var data = Encoding.UTF8.GetBytes(json);
        return webSocket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        if (webSocket.State != WebSocketState.Open) return Task.CompletedTask;
        return webSocket.CloseAsync(status, reason, CancellationToken.None);
    }

    private static AgentResponseUpdate CreateSystemMessage(string message) => new()
    {
        Role = ChatRole.System,
        AuthorName = "d-system",
        Contents = [new TextContent(message)]
    };

    private static AgentResponseUpdate CreateErrorMessage(string errorMessage) => new()
    {
        Role = ChatRole.System,
        AuthorName = "d-system",
        Contents = [new ErrorContent(errorMessage)]
    };
}
