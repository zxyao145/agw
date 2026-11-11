using Agw.Events.Application.Persistence;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public partial class AgwDbContext : IEventsDbContext
{
    public DbSet<EventSource> EventSources => Set<EventSource>();

    public DbSet<EventSourceItem> EventSourceItems => Set<EventSourceItem>();

    public DbSet<EventRecord> Events => Set<EventRecord>();

    public DbSet<EventSubscription> EventSubscriptions => Set<EventSubscription>();

    public DbSet<EventDelivery> EventDeliveries => Set<EventDelivery>();

    public DbSet<EventDeliveryAttempt> EventDeliveryAttempts => Set<EventDeliveryAttempt>();

    public DbSet<EventSchedule> EventSchedules => Set<EventSchedule>();

    public DbSet<EventOutbox> EventOutbox => Set<EventOutbox>();
}
