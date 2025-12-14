using DSystem.Domain.Enums;

namespace DSystem.Api.Contracts;

public record ProjectTaskCreateRequest(Guid WorkflowId, string Description, string Input);

public record ProjectTaskUpdateRequest(string Description, string Input);

public record ProjectTaskReorderRequest(DateTime UpdateTimeUtc);

public record ProjectTaskResponse(
    Guid Id,
    Guid ProjectId,
    Guid WorkflowId,
    ProjectTaskStatus Status,
    string Description,
    string Input,
    string? OutputJson,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime);