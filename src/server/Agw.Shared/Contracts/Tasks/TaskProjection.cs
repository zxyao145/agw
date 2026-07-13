namespace Agw.Shared.Contracts.Tasks;

/// <summary>
/// Logical task projected from records. This is not an EF entity.
/// </summary>
public sealed class TaskProjection
{
    public Guid TaskId { get; init; }

    public Guid ProjectId { get; init; }

    public string ContextId { get; init; } = string.Empty;

    public Guid? JobId { get; init; }

    public string Title { get; init; } = "Untitled";

    public TaskExecutionStatus Status { get; init; } = TaskExecutionStatus.Pending;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreateTime { get; init; }

    public DateTimeOffset? UpdateTime { get; init; }

    public DateTimeOffset? FinishedTime { get; init; }
}
