using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application;

public sealed class EventContextFacade : IEventContextFacade
{
    private readonly IEventsDbContext _db;

    public EventContextFacade(IEventsDbContext db)
    {
        _db = db;
    }

    public Task<EventContext?> GetAsync(Guid eventId, CancellationToken cancellationToken) =>
        _db
            .Events.AsNoTracking()
            .Where(row => row.Id == eventId)
            .Select(record => new EventContext
            {
                EventId = record.Id,
                SourceId = record.SourceId,
                ChainId = record.ChainId,
                ParentEventId = record.ParentEventId,
                Hops = record.Hops,
            })
            .SingleOrDefaultAsync(cancellationToken);
}
