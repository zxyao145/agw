using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_source_item")]
[EntityTypeConfiguration(typeof(EventSourceItemConfiguration))]
public class EventSourceItem
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    public string ItemKey { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public long ChangeSeq { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public Guid? LastEventId { get; set; }
}
