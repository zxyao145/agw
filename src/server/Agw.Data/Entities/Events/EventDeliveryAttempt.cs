using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_delivery_attempt")]
[EntityTypeConfiguration(typeof(EventDeliveryAttemptConfiguration))]
public class EventDeliveryAttempt
{
    public Guid Id { get; set; }

    public Guid DeliveryId { get; set; }

    public int Attempt { get; set; }

    public Guid? TurnId { get; set; }

    public string HostId { get; set; } = string.Empty;

    public Guid? GenerationId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndedAt { get; set; }

    public EventDeliveryAttemptResult Result { get; set; }

    public string? Error { get; set; }
}
