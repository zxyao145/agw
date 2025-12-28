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
    /// Execute ClaudeCode query with WebSocket streaming.
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
            // Receive the execution request from client
            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                HttpContext.RequestAborted);

            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed by client",
                    HttpContext.RequestAborted);
                return;
            }

            // Parse the request
            var requestJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
            var request = JsonUtil.Deserialize<ClaudeCodeExecuteRequest>(requestJson);

            if (request == null || string.IsNullOrWhiteSpace(request.Input))
            {
                var errorMessage = Encoding.UTF8.GetBytes(JsonUtil.Serialize(new
                {
                    type = "error",
                    content = "Invalid request: Input is required",
                    isError = true
                }));

                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorMessage),
                    WebSocketMessageType.Text,
                    true,
                    HttpContext.RequestAborted);

                await webSocket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Invalid request",
                    HttpContext.RequestAborted);
                return;
            }

            // Stream ClaudeCode responses back to client
            await foreach (var message in _claudeCodeService.ExecuteStreamingAsync(
                prompt: request.Input,
                workingDirectory: request.WorkingDirectory,
                apiKey: request.ApiKey,
                baseUrl: request.BaseUrl,
                systemPrompt: request.SystemPrompt,
                maxTurns: request.MaxTurns,
                cancellationToken: HttpContext.RequestAborted))
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

            // Close the connection normally
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Execution completed",
                    HttpContext.RequestAborted);
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
        catch (Exception ex)
        {
            // Send error message to client
            if (webSocket.State == WebSocketState.Open)
            {
                var errorMessage = Encoding.UTF8.GetBytes(JsonUtil.Serialize(new
                {
                    type = "error",
                    content = ex.Message,
                    isError = true,
                    errorMessage = ex.Message
                }));

                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorMessage),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);

                await webSocket.CloseAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "Error during execution",
                    CancellationToken.None);
            }
        }
    }
}
