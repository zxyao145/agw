using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Notifications;

[Table("notification")]
[EntityTypeConfiguration(typeof(NotificationConfiguration))]
public sealed class Notification : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }

    public NotificationKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Link { get; set; }

    public Guid? OriginExtensionId { get; set; }

    public string CallerKey { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string CorrelationKey { get; set; } = string.Empty;

    public Guid? ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }

    public NotificationState State { get; set; } = NotificationState.Open;

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public long Version { get; set; } = 1;

    [NotMapped]
    public NotificationQuestion? Question { get; set; }
}
