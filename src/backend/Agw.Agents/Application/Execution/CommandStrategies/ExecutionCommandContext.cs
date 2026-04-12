using System.Net.WebSockets;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.Models;
using Agw.Shared.Utils;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Application.Execution.CommandStrategies;

public sealed class ExecutionCommandContext
{
    public Guid AgentId { get; private set; }

    public string CurrentUser { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public WebSocket WebSocket { get; private set; }

    public SemaphoreSlim SendLock { get; private set; }

    public ExecutionConnectionState ConnectionState { get; private set; }

    public ExecutionCommandContext(
        Guid agentId,
        string currentUser,
        CancellationToken cancellationToken,
        WebSocket webSocket,
        SemaphoreSlim sendLock
        )
    {
        AgentId = agentId;
        CurrentUser = currentUser;
        CancellationToken = cancellationToken;
        WebSocket = webSocket;
        SendLock = sendLock;
        ConnectionState = new ExecutionConnectionState();
    }

    public AgentExecSession? AgentSession { get; set; }

    public required Func<WebSocketCloseStatus, string, Task> CloseConnectionAsync { get; init; }

    /// <summary>
    /// Avoid WebSocket execution tasks when they are fire-and-forget
    /// </summary>
    public required Action<Task> ObserveTurn { get; init; }


    public Task SendErrorAsync(string errorMessage, CancellationToken? cancellationToken = null)
    {
        return SendJsonAsync(JsonUtil.Serialize(CreateErrorMessage(errorMessage)), cancellationToken ?? CancellationToken);
    }

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


    public Task SendSystemMessageAsync(string message, CancellationToken? cancellationToken = null)
    {
        var payload = new AgwMessage(
            Guid.NewGuid().ToString("D"),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { new AgwTextContent { Content = message } });
        return SendJsonAsync(JsonUtil.Serialize(payload), cancellationToken ?? CancellationToken);
    }


    private async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        if (WebSocket.State != WebSocketState.Open) return;

        await SendLock.WaitAsync(cancellationToken);
        try
        {
            if (WebSocket.State != WebSocketState.Open) return;

            var data = Encoding.UTF8.GetBytes(json);
            await WebSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            SendLock.Release();
        }
    }


    public string ExtractReason(IActionResult result)
    {
        return result switch
        {
            ObjectResult objectResult when objectResult.Value is string message => message,
            StatusCodeResult statusCodeResult => $"Request failed with status {statusCodeResult.StatusCode}.",
            _ => "Invalid request payload"
        };
    }
}
