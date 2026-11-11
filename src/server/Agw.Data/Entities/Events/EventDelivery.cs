using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_delivery")]
[EntityTypeConfiguration(typeof(EventDeliveryConfiguration))]
public class EventDelivery : BaseEntity
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public Guid SubscriptionId { get; set; }

    public EventDeliveryState State { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public Guid? TurnId { get; set; }

    [JsonIgnore]
    [Encrypted]
    public string? RequestSnapshotJson { get; set; }

    public string? HandlerReleaseHash { get; set; }

    public long? HandlerEpoch { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset RecoverAfter { get; set; }

    public long Version { get; set; }
}
