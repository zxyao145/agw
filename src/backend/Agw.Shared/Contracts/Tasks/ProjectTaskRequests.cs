using Agw.Shared.Enums;
using Agw.Shared.Models;

namespace Agw.Shared.Contracts.Tasks;

public record ProjectTaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null);

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
