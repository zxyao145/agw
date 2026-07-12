using Agw.Shared.AgwMsgVm;

namespace Agw.Shared.Contracts.Tasks;

public record TaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null);

public record ProjectContextTitleUpdateRequest(string Title);

public record ProjectContextSummaryResponse(
    string ProjectId,
    string ContextId,
    Guid? JobId,
    string Title,
    TaskExecutionStatus? LatestStatus,
    int ExecutionCount,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime,
    string? ErrorMessage);

public record ProjectContextResponse(
    string ProjectId,
    string ContextId,
    Guid? JobId,
    TaskExecutionStatus? LatestStatus,
    int ExecutionCount,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime,
    string? ErrorMessage,
    IReadOnlyList<AgwMessage>? Messages);
