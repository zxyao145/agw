using Agw.Shared.Enums;
using Agw.Shared.Models;

namespace Agw.Shared.Contracts;

public record ProjectTaskCreateRequest(
    AgentRuntimeType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    string Description,
    string Input,
    string? Title = null,
    string? ContextId = null);

public record ProjectTaskUpdateRequest(string Description, string Input);

public record ProjectTaskReorderRequest(DateTime UpdateTimeUtc);

public record ProjectTaskSummaryResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    AgentRuntimeType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    ProjectTaskStatus Status,
    string Title,
    string Description,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? FinishedTime,
    DateTime? StartedTime);

public record ProjectTaskResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    AgentRuntimeType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    ProjectTaskStatus Status,
    string Title,
    string Description,
    string Input,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages);
