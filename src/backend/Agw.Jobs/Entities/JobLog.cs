using Agw.Shared;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Domain.Entities;

[Table("job_log")]
public class JobLog : BaseEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }

    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public bool Success { get; set; }
    public int Attempt { get; set; }
    public string? ErrorMessage { get; set; }
}
