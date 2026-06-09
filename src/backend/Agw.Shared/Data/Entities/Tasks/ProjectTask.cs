using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Contracts.Tasks;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_task")]
public class ProjectTask : BaseEntity
{
    // task 是 context 中某个具体的任务
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
