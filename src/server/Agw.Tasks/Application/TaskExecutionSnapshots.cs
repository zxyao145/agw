using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;

namespace Agw.Tasks.Application;

public record TaskExecutionSummary(
    Guid TaskId,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    TaskExecutionStatus Status,
    string Title,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? FinishedTime,
    DateTime? StartedTime);

public record TaskExecutionSnapshot(
    Guid TaskId,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    TaskExecutionStatus Status,
    string Title,
    string Input,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages);
