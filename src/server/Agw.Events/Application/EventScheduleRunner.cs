using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Domain.Behaviors;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application;

public sealed class EventScheduleRunner
{
    private readonly IEventsDbContext _db;
    private readonly IEventIngestTransaction _transaction;
    private readonly IEventScheduledPublisher _publisher;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;

    public EventScheduleRunner(
        IEventsDbContext db,
        IEventIngestTransaction transaction,
        IEventScheduledPublisher publisher,
        IApplicationLock applicationLock,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _transaction = transaction;
        _publisher = publisher;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
    }

    public async Task<bool> RunAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var identity = await _db
            .EventSchedules.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == scheduleId, cancellationToken);
        if (identity is null)
            return false;
        await using var lease = await _applicationLock.AcquireAsync(
            $"events:schedule:{UserInfoUtil.RequiredUserId}:{identity.CallerKey}:{identity.BusinessKey}",
            cancellationToken
        );
        var schedule = await _db.EventSchedules.SingleAsync(row => row.Id == scheduleId, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        if (schedule.State != EventScheduleState.Active || schedule.DueAt is not { } planned || planned > now)
            return false;
        var draft = new CloudEventDraft
        {
            Id = $"{schedule.Id}:{schedule.Revision}:{schedule.Occurrence + 1}",
            Type = schedule.EventType!,
            Time = planned,
            Subject = schedule.Subject,
            DataContentType = schedule.DataContentType,
            DataSchema = schedule.DataSchema,
            Data = CloudEventDataCodec.ReadData(schedule.Data, schedule.DataContentType, schedule.DataIsBinary),
            Scope = schedule.Scope,
            Tags = schedule.TagsJson is null ? null : JsonSerializer.Deserialize<string[]>(schedule.TagsJson),
            Summary = schedule.Summary,
            Link = schedule.Link,
            ParentEventId = schedule.ParentEventId,
        };
        await _transaction.ExecuteAsync(
            async token =>
            {
                await _publisher.StageAsync(schedule, draft, token);
                new EventScheduleBehavior(schedule).Fire(planned, NextDueAt(schedule, now));
                lease.HandleLostToken.ThrowIfCancellationRequested();
            },
            cancellationToken
        );
        return true;
    }

    private static DateTimeOffset? NextDueAt(EventSchedule schedule, DateTimeOffset now) =>
        schedule.Kind switch
        {
            EventScheduleKind.Once => null,
            EventScheduleKind.Interval => now.Add(schedule.Interval!.Value),
            EventScheduleKind.Cron => CronExpression
                .Parse(schedule.Cron!, CronFormat.Standard)
                .GetNextOccurrence(now, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone!)),
            _ => throw new AgwException(ErrorCodes.InvalidParam, "An active event schedule must have a kind."),
        };
}
