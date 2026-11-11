using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Notifications;

[Table("notification_dispatch")]
[EntityTypeConfiguration(typeof(NotificationDispatchConfiguration))]
public sealed class NotificationDispatch
{
    public Guid Id { get; set; }

    public Guid NotificationId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    public NotificationDispatchState State { get; set; } = NotificationDispatchState.Pending;

    public string? ExternalMessageId { get; set; }

    public long MessageVersion { get; set; }

    public long SentVersion { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public string? ClaimedBy { get; set; }

    public Guid? ClaimAttemptId { get; set; }

    public DateTimeOffset? ClaimExpiresAt { get; set; }

    public DateTimeOffset RecoverAfter { get; set; }
}
