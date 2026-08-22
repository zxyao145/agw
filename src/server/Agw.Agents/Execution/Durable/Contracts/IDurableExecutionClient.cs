using Agw.Agents.Execution.Connections;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Execution.Durable;

public sealed record DurableExecutionRequest(
    Guid ExecutionId,
    string UserId,
    Guid AgentId,
    AgentRuntimeType AgentType,
    AgwUserInput Input,
    TaskProjection Task,
    ExecutionSettings Settings
);

public sealed record DurableExecutionOutcome(Guid ExecutionId, DurableExecutionStatus Status, string? ErrorMessage);

public sealed record DurableExecutionEvent(string Cursor, AgwMessage Message);

public interface IDurableExecutionClient
{
    Task StartAsync(DurableExecutionRequest request, CancellationToken cancellationToken);

    Task<DurableExecutionOutcome> GetOutcomeAsync(Guid executionId, string userId, CancellationToken cancellationToken);

    Task<DurableExecutionOutcome> WaitForActionableOutcomeAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    );

    IAsyncEnumerable<DurableExecutionEvent> ReadAsync(
        Guid executionId,
        string userId,
        string? afterCursor,
        CancellationToken cancellationToken
    );

    Task<bool> InterruptAsync(Guid executionId, string userId, string? reason, CancellationToken cancellationToken);
}
