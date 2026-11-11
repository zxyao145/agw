using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Events;

public sealed class EventScheduledPublisher : IEventScheduledPublisher
{
    private readonly EventStaging _events;
    private readonly AgwDbContext _db;

    public EventScheduledPublisher(EventStaging events, AgwDbContext db)
    {
        _events = events;
        _db = db;
    }

    public async Task StageAsync(EventSchedule schedule, CloudEventDraft draft, CancellationToken cancellationToken)
    {
        var request = new EventStagingRequest
        {
            PublisherKey = "agw.scheduler:" + schedule.CallerKey,
            Event = draft,
            InheritParentHops = true,
            ChainId = schedule.ParentEventId.HasValue ? schedule.ChainId : null,
            Hops = schedule.Hops,
        };
        if (schedule.CallerKey.StartsWith("source:", StringComparison.Ordinal))
        {
            var sourceId = Guid.Parse(schedule.CallerKey[7..]);
            var source =
                await _db.EventSources.SingleOrDefaultAsync(
                    row =>
                        row.Id == sourceId && row.Kind == EventSourceKind.Timer && row.Enabled && row.DeletedAt == null,
                    cancellationToken
                ) ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
            await _events.StageSourceAsync(source, draft with { Source = source.CeSource }, request, cancellationToken);
        }
        else
            await _events.StageAsync(request, cancellationToken);
    }
}
