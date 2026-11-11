using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Notifications;

public sealed class NotificationUpdateReceiptConfiguration : IEntityTypeConfiguration<NotificationUpdateReceipt>
{
    public void Configure(EntityTypeBuilder<NotificationUpdateReceipt> builder)
    {
        builder.HasKey(receipt => new { receipt.NotificationId, receipt.RequestVersion });
        builder.Property(receipt => receipt.RequestHash).IsRequired().HasMaxLength(64);
    }
}
