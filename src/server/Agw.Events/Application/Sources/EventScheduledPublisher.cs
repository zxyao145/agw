using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Sources;

public sealed class EventScheduledPublisher : IEventScheduledPublisher
{
    private readonly IEventStaging _events;
    private readonly IEventRecordStaging _records;
    private readonly IEventsDbContext _db;

    public EventScheduledPublisher(IEventStaging events, IEventRecordStaging records, IEventsDbContext db)
    {
        _events = events;
        _records = records;
        _db = db;
    }

    public async Task StageAsync(EventSchedule schedule, CloudEventDraft draft, CancellationToken cancellationToken)
    {
        var request = new EventStagingRequest
        {
            Event = draft,
            InheritParentHops = true,
            ChainId = schedule.ParentEventId.HasValue ? schedule.ChainId : null,
            Hops = schedule.Hops,
        };
        if (schedule.CallerKey.StartsWith(EventTimerScheduleSync.SourceKeyPrefix, StringComparison.Ordinal))
        {
            if (
                !Guid.TryParse(
                    schedule.CallerKey.AsSpan(EventTimerScheduleSync.SourceKeyPrefix.Length),
                    out var sourceId
                )
            )
                throw new AgwException(ErrorCodes.InvalidParam);
            var source =
                await _db.EventSources.SingleOrDefaultAsync(
                    row =>
                        row.Id == sourceId && row.Kind == EventSourceKind.Timer && row.Enabled && row.DeletedAt == null,
                    cancellationToken
                ) ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
            await _records.StageSourceAsync(
                source,
                draft with
                {
                    Source = source.CeSource,
                },
                request,
                cancellationToken
            );
        }
        else if (schedule.CallerKey.StartsWith(EventManagedBy.ExtensionPrefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(schedule.CallerKey.AsSpan(EventManagedBy.ExtensionPrefix.Length), out var extensionId))
                throw new AgwException(ErrorCodes.InvalidParam);
            await _events.StageAsync(request with { ExtensionId = extensionId }, cancellationToken);
        }
        else
            throw new AgwException(ErrorCodes.InvalidParam);
    }
}
