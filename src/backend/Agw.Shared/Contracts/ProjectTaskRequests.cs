using Agw.Shared.Enums;
using Agw.Shared.Models;

namespace Agw.Shared.Contracts;

public record ProjectTaskCreateRequest(
    ProjectTaskAgentType AgentType,
    Guid? AgentflowId,
    Guid? AgentId,
    string Description,
    string Input,
    string? SessionId = null,
    string? Title = null,
    string? SystemPrompt = null,
    string? ContextId = null);

public record ProjectTaskUpdateRequest(string Description, string Input);

public record ProjectTaskReorderRequest(DateTime UpdateTimeUtc);

public record ProjectTaskResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
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
    DateTime? FinishedTime,
    int MessageCount,
    IReadOnlyList<AiMessage>? Messages);
