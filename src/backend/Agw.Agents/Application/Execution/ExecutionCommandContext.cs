using System.Net.WebSockets;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Utils;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Application.Execution;

public sealed class ExecutionCommandContext
{
    /// <summary>
    /// Agent or agentflow identifier bound to this WebSocket execution connection.
    /// </summary>
    public Guid AgentId { get; private set; }

    /// <summary>
    /// User name captured when the socket was accepted; used when creating or resolving execution tasks.
    /// </summary>
    public string CurrentUser { get; private set; }

    /// <summary>
    /// Request cancellation token for the lifetime of the WebSocket request.
    /// </summary>
    public CancellationToken CancellationToken { get; private set; }

    /// <summary>
    /// Accepted WebSocket used by command strategies to stream messages back to the client.
    /// </summary>
    public WebSocket WebSocket { get; private set; }

    /// <summary>
    /// Shared send gate for all writers on this socket.
    /// </summary>
    public SemaphoreSlim SendLock { get; private set; }

    /// <summary>
    /// Mutable connection state shared by all command strategies for this socket.
    /// </summary>
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

    /// <summary>
    /// Reusable agent runtime session for agent executions. Agentflow executions do not populate this.
    /// </summary>
    public AgentExecSession? AgentSession { get; set; }

    /// <summary>
    /// Controller-owned close callback so strategies can terminate the socket without owning close semantics.
    /// </summary>
    public required Func<WebSocketCloseStatus, string, Task> CloseConnectionAsync { get; init; }

    /// <summary>
    /// Observes background execution turns so fire-and-forget streaming tasks cannot fail silently.
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
                Constants.DefaultAgentAuthor,
                AiRole.System,
                new List<AgwContent> { content }
            );
        return payload;
    }


    public Task SendSystemMessageAsync(string message, CancellationToken? cancellationToken = null)
    {
        var payload = new AgwMessage(
            Guid.NewGuid().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            new List<AgwContent> { new AgwTextContent { Content = message } });
        return SendJsonAsync(JsonUtil.Serialize(payload), cancellationToken ?? CancellationToken);
    }


    private async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        if (WebSocket.State != WebSocketState.Open) return;

        // Command strategies and active turns can both write to the socket, so every send must take the lock.
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
