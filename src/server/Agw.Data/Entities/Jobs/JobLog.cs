using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Jobs;

[Table("job_log")]
[EntityTypeConfiguration(typeof(JobLogConfiguration))]
public class JobLog : BaseEntity
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid TaskId { get; set; }

    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public bool Success { get; set; }
    public int Attempt { get; set; }
    public string? ErrorMessage { get; set; }
}
