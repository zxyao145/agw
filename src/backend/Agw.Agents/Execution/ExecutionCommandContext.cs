using System.Net.WebSockets;

using Agw.Agents.Application;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Execution;

public sealed class ExecutionCommandContext
{
    public Guid AgentId { get; init; }

    public required ExecutionConnectionState ConnectionState { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public required string CurrentUser { get; init; }

    public required IAgentExecutionCoordinator ExecutionCoordinator { get; init; }

    public required WebSocket WebSocket { get; init; }

    public required SemaphoreSlim SendLock { get; init; }

    public AgentExecSession? AgentSession { get; set; }

    public Func<string, Task>? SendErrorAsync { get; init; }

    public Func<string, Task>? SendSystemMessageAsync { get; init; }

    public Func<WebSocketCloseStatus, string, Task>? CloseConnectionAsync { get; init; }

    public Func<IActionResult, string>? ExtractReason { get; init; }

    public Action<Task>? ObserveExecution { get; init; }
}
