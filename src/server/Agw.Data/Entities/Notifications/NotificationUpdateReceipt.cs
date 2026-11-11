using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Notifications;

[Table("notification_update_receipt")]
[EntityTypeConfiguration(typeof(NotificationUpdateReceiptConfiguration))]
public sealed class NotificationUpdateReceipt
{
    public Guid NotificationId { get; set; }

    public long RequestVersion { get; set; }

    public string RequestHash { get; set; } = string.Empty;
}
