using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Notifications;

public sealed class NotificationQuestionConfiguration : IEntityTypeConfiguration<NotificationQuestion>
{
    public void Configure(EntityTypeBuilder<NotificationQuestion> builder)
    {
        builder.HasKey(question => question.NotificationId);
        builder.Property(question => question.OptionsJson).IsRequired();
        builder.Property(question => question.AnsweredBy).HasMaxLength(200);
        builder.Property(question => question.AnsweredVia).HasMaxLength(400);
    }
}
