using DSystem.Shared.Enums;

namespace DSystem.Shared.Contracts;

public record ProjectTaskCreateRequest(
    ProjectTaskAgentType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    string Description,
    string Input);

public record ProjectTaskUpdateRequest(string Description, string Input);

public record ProjectTaskReorderRequest(DateTime UpdateTimeUtc);

public record ProjectTaskResponse(
    Guid Id,
    Guid ProjectId,
    ProjectTaskAgentType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    ProjectTaskStatus Status,
    string Description,
    string Input,
    string? OutputJson,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime);
