using DSystem.Shared;
using DSystem.Shared.Enums;

namespace DSystem.Domain.Entities;

public class ProjectTask : BaseEntity
{
    public Guid Id { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// ContextId 全局唯一
    /// </summary>
    public string ContextId { get; set; } = string.Empty;

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

    public ICollection<TaskRecord> ConversationList { get; set; } = [];
}
