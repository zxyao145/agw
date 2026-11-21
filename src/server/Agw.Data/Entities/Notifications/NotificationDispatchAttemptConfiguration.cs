using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Notifications;

public sealed class NotificationDispatchAttemptConfiguration : IEntityTypeConfiguration<NotificationDispatchAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationDispatchAttempt> builder)
    {
        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Result).HasConversion<int>();
        builder.Property(attempt => attempt.ExternalMessageId).HasMaxLength(400);
        builder.Property(attempt => attempt.Error).HasMaxLength(2000);
        builder.HasIndex(attempt => new { attempt.DispatchId, attempt.StartedAt });
    }
}
