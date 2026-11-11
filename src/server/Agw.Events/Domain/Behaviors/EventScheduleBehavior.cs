using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Domain.Behaviors;

public sealed class EventScheduleBehavior
{
    private readonly EventSchedule _schedule;

    public EventScheduleBehavior(EventSchedule schedule)
    {
        _schedule = schedule;
    }

    public bool Upsert(long requestSeq, EventScheduleDefinition definition)
    {
        if (!CanApply(requestSeq))
        {
            return requestSeq == _schedule.RequestSeq;
        }

        _schedule.Kind = definition.Kind;
        _schedule.DueAt = definition.DueAt;
        _schedule.Interval = definition.Interval;
        _schedule.Cron = definition.Cron;
        _schedule.TimeZone = definition.TimeZone;
        _schedule.EventType = definition.EventType;
        _schedule.Subject = definition.Subject;
        _schedule.DataContentType = definition.DataContentType;
        _schedule.DataSchema = definition.DataSchema;
        _schedule.Summary = definition.Summary;
        _schedule.Link = definition.Link;
        _schedule.Data = definition.Data;
        _schedule.DataIsBinary = definition.DataIsBinary;
        _schedule.Scope = definition.Scope;
        _schedule.TagsJson = definition.TagsJson;
        _schedule.ChainId = definition.ChainId;
        _schedule.ParentEventId = definition.ParentEventId;
        _schedule.Hops = definition.Hops;
        _schedule.State = EventScheduleState.Active;
        _schedule.RequestSeq = requestSeq;
        _schedule.Revision++;
        _schedule.Occurrence = 0;
        _schedule.LastFiredAt = null;
        _schedule.Version++;
        return true;
    }

    public bool Cancel(long requestSeq)
    {
        if (!CanApply(requestSeq))
        {
            return requestSeq == _schedule.RequestSeq;
        }

        _schedule.DueAt = null;
        _schedule.State = EventScheduleState.Cancelled;
        _schedule.RequestSeq = requestSeq;
        _schedule.Revision++;
        _schedule.Version++;
        return true;
    }

    private bool CanApply(long requestSeq) => _schedule.Revision == 0 || requestSeq > _schedule.RequestSeq;

    public void Fire(DateTimeOffset plannedAt, DateTimeOffset? nextDueAt)
    {
        if (_schedule.State != EventScheduleState.Active || _schedule.DueAt != plannedAt)
            throw new AgwException(ErrorCodes.InvalidParam, "The schedule is not active at the planned time.");
        _schedule.Occurrence++;
        _schedule.LastFiredAt = plannedAt;
        _schedule.DueAt = nextDueAt;
        _schedule.State = nextDueAt is not null ? EventScheduleState.Active : EventScheduleState.Completed;
        _schedule.Version++;
    }
}
