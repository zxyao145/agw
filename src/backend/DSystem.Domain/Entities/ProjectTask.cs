using DSystem.Shared;
using DSystem.Shared.Enums;

namespace DSystem.Domain.Entities;

public class ProjectTask : BaseEntity
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public ProjectTaskAgentType AgentType { get; set; } = ProjectTaskAgentType.Agentflow;
    public Guid? AgentflowId { get; set; }
    public Guid? AgentId { get; set; }

    public string Description { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Task execution status.
    /// </summary>
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.Pending;

    /// <summary>
    /// User input to be executed by the associated target.
    /// </summary>
    public string Input { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public DateTime? StartedTime { get; set; }
    public DateTime? FinishedTime { get; set; }

}
