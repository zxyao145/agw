using Agw.Notifications.Application.Persistence;
using Agw.Shared.Data.Entities.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public partial class AgwDbContext : INotificationsDbContext
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationQuestion> NotificationQuestions => Set<NotificationQuestion>();

    public DbSet<NotificationDispatch> NotificationDispatches => Set<NotificationDispatch>();

    public DbSet<NotificationDispatchAttempt> NotificationDispatchAttempts => Set<NotificationDispatchAttempt>();

    public DbSet<NotificationUpdateReceipt> NotificationUpdateReceipts => Set<NotificationUpdateReceipt>();
}
