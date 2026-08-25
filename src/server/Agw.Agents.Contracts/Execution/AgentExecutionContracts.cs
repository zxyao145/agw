using Agw.Agents.Contracts.Messages;
using Agw.Projects.Contracts.Execution;

namespace Agw.Agents.Contracts.Execution;

public enum AgentTargetKind
{
    Agent = 0,
    Agentflow = 1,
}

public enum AgentExecutionState
{
    Queued = 0,
    Running = 1,
    WaitingForHuman = 2,
    Completed = 3,
    Failed = 4,
    Interrupted = 5,
}

public enum HumanInteractionPolicy
{
    Allow = 0,
    Reject = 1,
}

public sealed record AgentTarget(AgentTargetKind Kind, Guid? Id = null, string? Name = null);

public sealed record AgentExecutionRequest(
    Guid ExecutionId,
    string OwnerUserId,
    AgentTarget Target,
    ProjectTaskSnapshot Task,
    AgwUserInput Input,
    bool Resume = false,
    HumanInteractionPolicy HumanInteractionPolicy = HumanInteractionPolicy.Allow
);

public sealed record AgentExecutionResult(
    Guid ExecutionId,
    AgentExecutionState State,
    IReadOnlyList<AgwMessage> Messages,
    string? ErrorMessage = null
);

public sealed record AgentExecutionEvent(string? Cursor, AgwMessage Message);

public interface IAgentExecutionFacade
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

public interface IDurableAgentExecutionFacade
{
    Task<AgentExecutionResult> GetOutcomeAsync(
        Guid executionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
        Guid executionId,
        string ownerUserId,
        string? afterCursor,
        CancellationToken cancellationToken = default
    );

    Task<bool> InterruptAsync(
        Guid executionId,
        string ownerUserId,
        string reason,
        CancellationToken cancellationToken = default
    );
}
