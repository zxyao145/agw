using Agw.Shared.Models;

namespace Agw.Shared.Contracts.Tasks;

public record ProjectTaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null);

public record ProjectTaskTitleUpdateRequest(string Title);

public record ProjectTaskSummaryResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    ProjectTaskStatus Status,
    string Title,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? FinishedTime,
    DateTime? StartedTime);

public record ProjectTaskResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    ProjectTaskStatus Status,
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
    string Title,
    Guid? LatestTaskId,
    ProjectTaskStatus? LatestStatus,
    int TaskCount,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime,
    string? ErrorMessage);

public record ProjectContextResponse(
    string ProjectId,
    string ContextId,
    Guid? LatestTaskId,
    IReadOnlyList<ProjectTaskSummaryResponse> Tasks,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages);
