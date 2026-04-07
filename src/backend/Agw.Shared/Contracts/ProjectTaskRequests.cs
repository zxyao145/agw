using Agw.Shared.Models;
using Agw.Shared.Enums;

namespace Agw.Shared.Contracts;

public record ProjectTaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null)
{
    [Obsolete("Project tasks no longer persist task-level target bindings.")]
    public ProjectTaskCreateRequest(
        AgentRuntimeType AgentType,
        Guid? AgentflowId,
        Guid? AgentId,
        string Description,
        string Input,
        string? Title = null,
        string? ContextId = null)
        : this(null, Input, Title, ContextId)
    {
    }
}

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
