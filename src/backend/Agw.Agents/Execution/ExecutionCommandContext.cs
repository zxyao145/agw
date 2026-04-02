using Agw.Agents.Application;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;

namespace Agw.Api.Execution;

public sealed class ExecutionCommandContext
{
    public required Guid AgentId { get; init; }

    public required ExecutionConnectionState ConnectionState { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    public required string CurrentUser { get; init; }

    public required IAgentExecutionCoordinator ExecutionCoordinator { get; init; }

    public required WebSocket WebSocket { get; init; }

    public required SemaphoreSlim SendLock { get; init; }

    public AgentExecSession? AgentSession { get; set; }

    public required Func<string, Task> SendErrorAsync { get; init; }

    public required Func<string, Task> SendSystemMessageAsync { get; init; }

    public required Func<WebSocketCloseStatus, string, Task> CloseConnectionAsync { get; init; }

    public required Func<IActionResult, string> ExtractReason { get; init; }

    public required Action<Task> ObserveExecution { get; init; }
}
