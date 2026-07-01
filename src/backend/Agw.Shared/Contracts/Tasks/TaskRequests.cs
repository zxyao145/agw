using Agw.Shared.AgwMsgVm;

namespace Agw.Shared.Contracts.Tasks;

public record TaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null);

public record ProjectContextTitleUpdateRequest(string Title);

public record TaskSummaryResponse(
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

public record TaskResponse(
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

public record ProjectContextSummaryResponse(
    string ProjectId,
    string ContextId,
    Guid? JobId,
    string Title,
    Guid? LatestTaskId,
    TaskExecutionStatus? LatestStatus,
    int TaskCount,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime,
    string? ErrorMessage);

public record ProjectContextResponse(
    string ProjectId,
    string ContextId,
    Guid? JobId,
    Guid? LatestTaskId,
    IReadOnlyList<TaskSummaryResponse> Tasks,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages);
