using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Contracts;

public record TaskExecutionSummary(
    Guid TaskId,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    TaskExecutionStatus Status,
    string Title,
    string? ErrorMessage,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime,
    DateTimeOffset? FinishedTime,
    DateTimeOffset? StartedTime
);

public record TaskExecutionSnapshot(
    Guid TaskId,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    TaskExecutionStatus Status,
    string Title,
    string Input,
    string? ErrorMessage,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime,
    DateTimeOffset? StartedTime,
    DateTimeOffset? FinishedTime,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages
);
