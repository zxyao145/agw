using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Notifications;

[Table("notification_question")]
[EntityTypeConfiguration(typeof(NotificationQuestionConfiguration))]
public sealed class NotificationQuestion
{
    public Guid NotificationId { get; set; }

    public string OptionsJson { get; set; } = "[]";

    public int? Recommend { get; set; }

    public bool AllowText { get; set; }

    public int? AnswerOption { get; set; }

    [Encrypted]
    public string? AnswerText { get; set; }

    public string? AnsweredBy { get; set; }

    public string? AnsweredVia { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }
}
