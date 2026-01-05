using ClaudeCodeWrapper.Models;
using DSystem.Api.Contracts;
using DSystem.ExternalAgents;
using DSystem.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/external-agents/claude-code")]
public class ClaudeCodeController : ControllerBase
{
    private readonly ClaudeCodeService _claudeCodeService;

    public ClaudeCodeController(ClaudeCodeService claudeCodeService)
    {
        _claudeCodeService = claudeCodeService;
    }

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

        try
        {
            // Keep connection open and process multiple messages
            while (webSocket.State == WebSocketState.Open)
            {
                // Receive the execution request from client
                var buffer = new byte[1024 * 4];
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    HttpContext.RequestAborted);

                // Handle close message
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed by client",
                        HttpContext.RequestAborted);
                    break;
                }

                // Parse the request
                var requestJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                ClaudeCodeExecuteRequest? request = null;

                try
                {
                    request = JsonUtil.Deserialize<ClaudeCodeExecuteRequest>(requestJson);
                }
                catch (JsonException ex)
                {
                    // Send error for invalid JSON
                    await SendErrorAsync(webSocket, $"Invalid JSON: {ex.Message}", keepOpen: true);
                    continue;
                }

                // Validate request
                if (request == null || string.IsNullOrWhiteSpace(request.Input))
                {
                    await SendErrorAsync(webSocket, "Invalid request: Input is required", keepOpen: true);
                    continue;
                }

                // Process the request and stream responses
                try
                {
                    await foreach (var message in _claudeCodeService.ExecuteStreamingAsync(
                        prompt: request.Input,
                        workingDirectory: request.WorkingDirectory,
                        apiKey: request.ApiKey,
                        baseUrl: request.BaseUrl,
                        systemPrompt: request.SystemPrompt,
                        maxTurns: request.MaxTurns,
                        sessionId: request.SessionId,
                        cancellationToken: HttpContext.RequestAborted
                        ))
                    {
                        if (webSocket.State != WebSocketState.Open)
                        {
                            break;
                        }

                        var json = JsonUtil.Serialize(message);
                        var data = Encoding.UTF8.GetBytes(json);

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(data),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            HttpContext.RequestAborted);
                    }
                }
                catch (Exception ex)
                {
                    // Send error message but keep connection open for next request
                    await SendErrorAsync(webSocket, $"Execution error: {ex.Message}", keepOpen: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or request was cancelled
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Request cancelled",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // WebSocket connection error - connection already closed
            // No need to close again
        }
        catch (Exception ex)
        {
            // Unexpected error - try to close gracefully
            if (webSocket.State == WebSocketState.Open)
            {
                await SendErrorAsync(webSocket, $"Unexpected error: {ex.Message}", keepOpen: false);

                await webSocket.CloseAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "Unexpected error",
                    CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Helper method to send error messages via WebSocket.
    /// </summary>
    private async Task SendErrorAsync(WebSocket webSocket, string errorMessage, bool keepOpen)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var errorData = Encoding.UTF8.GetBytes(JsonUtil.Serialize(new
        {
            type = "error",
            content = errorMessage,
            isError = true,
            errorMessage = errorMessage
        }));

        await webSocket.SendAsync(
            new ArraySegment<byte>(errorData),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }
}
