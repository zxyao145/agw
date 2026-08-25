using Agw.Shared.Data.Entities.Projects;

namespace Agw.Agents.Execution;

public sealed class AgentExecutionTask
{
    public Guid TaskId { get; init; }

    public Guid ProjectConversationId { get; init; }

    public Guid ProjectId { get; init; }

    public string ContextId { get; init; } = string.Empty;

    public Guid? JobId { get; init; }

    public string Title { get; init; } = "Untitled";

    public TaskExecutionStatus Status { get; init; } = TaskExecutionStatus.Pending;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreateTime { get; init; }

    public DateTimeOffset? UpdateTime { get; init; }

    public DateTimeOffset? FinishedTime { get; init; }
}
