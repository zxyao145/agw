using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Agw.Notifications.Application.Persistence;

public interface INotificationsDbContext : IModuleDbContext
{
    DbSet<Notification> Notifications { get; }

    DbSet<NotificationQuestion> NotificationQuestions { get; }

    DbSet<NotificationDispatch> NotificationDispatches { get; }

    DbSet<NotificationDispatchAttempt> NotificationDispatchAttempts { get; }

    DbSet<NotificationUpdateReceipt> NotificationUpdateReceipts { get; }
}
