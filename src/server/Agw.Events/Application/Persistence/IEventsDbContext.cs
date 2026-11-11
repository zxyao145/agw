using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Persistence;

public interface IEventsDbContext : IModuleDbContext
{
    DbSet<EventSource> EventSources { get; }

    DbSet<EventSourceItem> EventSourceItems { get; }

    DbSet<EventRecord> Events { get; }

    DbSet<EventSubscription> EventSubscriptions { get; }

    DbSet<EventDelivery> EventDeliveries { get; }

    DbSet<EventDeliveryAttempt> EventDeliveryAttempts { get; }

    DbSet<EventSchedule> EventSchedules { get; }

    DbSet<EventOutbox> EventOutbox { get; }
}
