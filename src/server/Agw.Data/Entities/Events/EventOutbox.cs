using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_outbox")]
[EntityTypeConfiguration(typeof(EventOutboxConfiguration))]
public class EventOutbox
{
    public Guid Id { get; set; }

    public string Topic { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public EventOutboxState State { get; set; }

    public string? TransportMessageId { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
