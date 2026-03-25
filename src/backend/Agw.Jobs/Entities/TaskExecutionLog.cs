using Agw.Shared;

namespace Agw.Domain.Entities;

public class TaskExecutionLog : BaseEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }

    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public bool Success { get; set; }
    public int Attempt { get; set; }
    public string? ErrorMessage { get; set; }
}
