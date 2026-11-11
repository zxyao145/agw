using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_schedule")]
[EntityTypeConfiguration(typeof(EventScheduleConfiguration))]
public class EventSchedule : BaseEntity
{
    public Guid Id { get; set; }

    public string CallerKey { get; set; } = string.Empty;

    public string BusinessKey { get; set; } = string.Empty;

    public EventScheduleKind? Kind { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public TimeSpan? Interval { get; set; }

    public string? Cron { get; set; }

    public string? TimeZone { get; set; }

    public string? EventType { get; set; }

    public string? Subject { get; set; }

    public string? DataContentType { get; set; }

    public string? DataSchema { get; set; }

    public string? Summary { get; set; }

    public string? Link { get; set; }

    [JsonIgnore]
    [Encrypted]
    public string? Data { get; set; }

    public bool DataIsBinary { get; set; }

    public string? Scope { get; set; }

    public string? TagsJson { get; set; }

    public Guid ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }

    public EventScheduleState State { get; set; }

    public long RequestSeq { get; set; }

    public long Revision { get; set; }

    public long Occurrence { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    public long Version { get; set; }
}
