using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Notifications;

[Table("notification_dispatch_attempt")]
[EntityTypeConfiguration(typeof(NotificationDispatchAttemptConfiguration))]
public sealed class NotificationDispatchAttempt
{
    public Guid Id { get; set; }

    public Guid DispatchId { get; set; }

    public int Attempt { get; set; }

    public long MessageVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public NotificationDispatchAttemptResult Result { get; set; }

    public string? ExternalMessageId { get; set; }

    public string? Error { get; set; }
}
