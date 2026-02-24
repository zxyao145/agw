using DSystem.Shared.Enums;

namespace DSystem.Shared.Contracts;

public record ProjectTaskCreateRequest(
    ProjectTaskAgentType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    string SessionId,
    string Title,
    string Description,
    string Input);

public record ProjectTaskUpdateRequest(string Description, string Input);

public record ProjectTaskReorderRequest(DateTime UpdateTimeUtc);

public record ProjectTaskResponse(
    Guid Id,
    string ProjectId,
    ProjectTaskAgentType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    ProjectTaskStatus Status,
    string SessionId,
    string Title,
    string Description,
    string Input,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime);
