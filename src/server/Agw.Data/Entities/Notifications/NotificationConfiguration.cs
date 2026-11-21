using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Notifications;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.CreateBy).IsRequired();
        builder.Property(notification => notification.Kind).HasConversion<int>();
        builder.Property(notification => notification.Title).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.Text).IsRequired().HasMaxLength(4000);
        builder.Property(notification => notification.Link).HasMaxLength(2000);
        builder.Property(notification => notification.CallerKey).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.IdempotencyKey).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.CorrelationKey).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.State).HasConversion<int>();
        builder.Property(notification => notification.ChangedAt).IsRequired();
        builder.Property(notification => notification.ParentEventId);
        builder.Property(notification => notification.Version).IsConcurrencyToken();
        builder
            .HasIndex(notification => new
            {
                notification.CreateBy,
                notification.CallerKey,
                notification.IdempotencyKey,
            })
            .IsUnique();
        builder.HasIndex(notification => new
        {
            notification.CreateBy,
            notification.ChangedAt,
            notification.Id,
        });
        builder.HasIndex(notification => new { notification.CreateBy, notification.State });
        builder.HasIndex(notification => new { notification.OriginExtensionId, notification.CorrelationKey });
    }
}
