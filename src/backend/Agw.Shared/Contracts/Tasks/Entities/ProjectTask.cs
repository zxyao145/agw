using Agw.Shared.Enums;
using System.ComponentModel.DataAnnotations.Schema;

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

    public Guid? JobId { get; set; }

    public string Title { get; set; } = "Untitled";

    /// <summary>
    /// Task execution status.
    /// </summary>
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.Pending;

    public string? ErrorMessage { get; set; }

    public DateTime? FinishedTime { get; set; }

    [NotMapped]
    [Obsolete("Project tasks no longer persist task-level target bindings.")]
    public AgentRuntimeType AgentType { get; set; } = AgentRuntimeType.Agent;

    [NotMapped]
    [Obsolete("Project tasks no longer persist task-level target bindings.")]
    public Guid? AgentId { get; set; }

    [NotMapped]
    [Obsolete("Project tasks no longer persist descriptions.")]
    public string Description { get; set; } = string.Empty;
}
