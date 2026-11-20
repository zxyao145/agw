using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceFacade : IEventSourceFacade
{
    private readonly IEventsDbContext _db;
    private readonly IEventPublisherSources _publisherSources;
    private readonly EventCaller _caller;

    public EventSourceFacade(IEventsDbContext db, IEventPublisherSources publisherSources, EventCaller caller)
    {
        _db = db;
        _publisherSources = publisherSources;
        _caller = caller;
    }

    public async Task<EventSourceIdentity?> GetAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await _db
            .EventSources.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == sourceId, cancellationToken);
        return source is null
            ? null
            : new EventSourceIdentity
            {
                SourceId = source.Id,
                Kind = source.Kind,
                Name = source.Name,
                ManagedBy = source.ManagedBy,
                ManagedKey = source.ManagedKey,
                Source = source.CeSource,
            };
    }

    public async Task<Guid> GetBuiltInSourceIdAsync(string name, CancellationToken cancellationToken)
    {
        var source = await _publisherSources.EnsureBuiltInAsync(name, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return source.Id;
    }

    public async Task<Guid> GetExtensionSourceIdAsync(CancellationToken cancellationToken)
    {
        var extension = _caller.RequiredExtension();
        var source = await _publisherSources.EnsureExtensionAsync(
            extension.GenerationId.ExtensionId,
            cancellationToken
        );
        await _db.SaveChangesAsync(cancellationToken);
        return source.Id;
    }
}
