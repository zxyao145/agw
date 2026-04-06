using Agw.Shared.Enums;

namespace Agw.Shared.Tasks.Entities;

public class ProjectTask : BaseEntity
{
    // as session id
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>
    /// ContextId 全局唯一
    /// </summary>
    public string ContextId { get; set; } = string.Empty;

    public AgentRuntimeType AgentType { get; set; } = AgentRuntimeType.Agent;

    /// <summary>
    /// if AgentType == Agent, AgentId == entity Agent.Id；
    /// if AgentType == Agentflow, AgentId == entity Agentflow.Id；
    /// </summary>
    public Guid? AgentId { get; set; }

    public string Title { get; set; } = "Untitled";

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The system prompt used by the orchestrator/router for this request.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Task execution status.
    /// </summary>
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.Pending;

    public string? ErrorMessage { get; set; }

    public DateTime? FinishedTime { get; set; }

}
