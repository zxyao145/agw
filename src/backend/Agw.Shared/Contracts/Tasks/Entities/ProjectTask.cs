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

    public Guid? JobId { get; set; }

    public string Title { get; set; } = "Untitled";

    /// <summary>
    /// Task execution status.
    /// </summary>
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.Pending;

    public string? ErrorMessage { get; set; }

    public DateTime? FinishedTime { get; set; }
}
