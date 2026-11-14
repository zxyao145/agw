using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Events.Domain.Behaviors;
using Agw.Events.Domain.ValueObjects;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Events.Application;

public sealed class EventScheduler : IEventScheduler
{
    private readonly IEventsDbContext _db;
    private readonly IExtensionCallerAccessor _callerAccessor;
    private readonly EventCaller _caller;
    private readonly IApplicationLock _applicationLock;
    private readonly IEventContextFacade _events;
    private readonly IEventTemplateValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventScheduler> _logger;

    public EventScheduler(
        IEventsDbContext db,
        IExtensionCallerAccessor callerAccessor,
        EventCaller caller,
        IApplicationLock applicationLock,
        IEventContextFacade events,
        IEventTemplateValidator validator,
        TimeProvider timeProvider,
        ILogger<EventScheduler> logger
    )
    {
        _db = db;
        _callerAccessor = callerAccessor;
        _caller = caller;
        _applicationLock = applicationLock;
        _events = events;
        _validator = validator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ScheduleState> UpsertAsync(ScheduleRequest request, CancellationToken cancellationToken)
    {
        ValidateKey(request.BusinessKey, request.RequestSeq);
        var callerKey = await CallerKeyAsync(request.BusinessKey, cancellationToken);
        await using var lease = await _applicationLock.AcquireAsync(
            $"events:schedule:{UserInfoUtil.RequiredUserId}:{callerKey}:{request.BusinessKey}",
            cancellationToken
        );
        var schedule = await FindAsync(callerKey, request.BusinessKey, cancellationToken);
        if (schedule is not null && schedule.RequestSeq >= request.RequestSeq)
            return State(schedule, schedule.RequestSeq == request.RequestSeq);
        if (_callerAccessor.Current is not null)
            _caller.ValidateType(_caller.RequiredExtension(), request.Event.Type, request.ParentEventId is null);
        else if (request.Event.Type.StartsWith("agw.", StringComparison.Ordinal))
            throw new AgwException(ErrorCodes.EventTypeReserved);
        _validator.Validate(request.Event with { Id = "template", Source = "/agw/schedules/" + callerKey });
        var parent = request.ParentEventId is { } parentId
            ? await _events.GetAsync(parentId, cancellationToken) ?? throw new AgwException(ErrorCodes.ResourceNotFound)
            : null;
        schedule ??= Create(callerKey, request.BusinessKey);
        var accepted = new EventScheduleBehavior(schedule).Upsert(
            request.RequestSeq,
            new EventScheduleDefinition
            {
                Kind = (EventScheduleKind)request.Kind,
                DueAt = DueAt(request),
                Interval = request.Interval,
                Cron = request.Cron,
                TimeZone = request.TimeZone,
                EventType = request.Event.Type,
                Subject = request.Event.Subject,
                DataContentType = request.Event.DataContentType,
                DataSchema = request.Event.DataSchema,
                Summary = request.Event.Summary,
                Link = request.Event.Link,
                Data = CloudEventDataCodec.StoreData(request.Event.Data),
                DataIsBinary = request.Event.Data?.Kind == CloudEventDataKind.Binary,
                Scope = request.Event.Scope,
                TagsJson = request.Event.Tags is null ? null : JsonSerializer.Serialize(request.Event.Tags),
                ChainId = parent?.ChainId ?? schedule.Id,
                ParentEventId = request.ParentEventId,
                Hops = parent?.Hops ?? 0,
            }
        );
        lease.HandleLostToken.ThrowIfCancellationRequested();
        await _db.SaveChangesAsync(cancellationToken);
        return State(schedule, accepted);
    }

    public async Task<ScheduleState> CancelAsync(
        string businessKey,
        long requestSeq,
        CancellationToken cancellationToken
    )
    {
        ValidateKey(businessKey, requestSeq);
        var callerKey = await CallerKeyAsync(businessKey, cancellationToken);
        await using var lease = await _applicationLock.AcquireAsync(
            $"events:schedule:{UserInfoUtil.RequiredUserId}:{callerKey}:{businessKey}",
            cancellationToken
        );
        var schedule = await FindAsync(callerKey, businessKey, cancellationToken) ?? Create(callerKey, businessKey);
        var accepted = new EventScheduleBehavior(schedule).Cancel(requestSeq);
        lease.HandleLostToken.ThrowIfCancellationRequested();
        await _db.SaveChangesAsync(cancellationToken);
        return State(schedule, accepted);
    }

    public async Task<ScheduleState?> GetAsync(string businessKey, CancellationToken cancellationToken)
    {
        var callerKey = await CallerKeyAsync(businessKey, cancellationToken);
        var schedule = await FindAsync(callerKey, businessKey, cancellationToken);
        return schedule is null ? null : State(schedule, true);
    }

    private async Task<string> CallerKeyAsync(string businessKey, CancellationToken cancellationToken)
    {
        if (_callerAccessor.Current is not null)
            return $"ext:{_caller.RequiredExtension().GenerationId.ExtensionId}";
        if (
            !businessKey.StartsWith("source:", StringComparison.Ordinal) || !Guid.TryParse(businessKey[7..], out var id)
        )
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        var owner = UserInfoUtil.RequiredUserId;
        if (
            !_db.EventSources.Local.Any(row =>
                row.Id == id && row.Kind == EventSourceKind.Timer && row.CreateBy == owner
            )
            && !await _db.EventSources.AnyAsync(
                row => row.Id == id && row.Kind == EventSourceKind.Timer,
                cancellationToken
            )
        )
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        return $"source:{id}";
    }

    private Task<EventSchedule?> FindAsync(string callerKey, string businessKey, CancellationToken cancellationToken) =>
        _db.EventSchedules.SingleOrDefaultAsync(
            row => row.CallerKey == callerKey && row.BusinessKey == businessKey,
            cancellationToken
        );

    private EventSchedule Create(string callerKey, string businessKey)
    {
        var schedule = new EventSchedule
        {
            Id = Guid.CreateVersion7(),
            CreateBy = UserInfoUtil.RequiredUserId,
            CallerKey = callerKey,
            BusinessKey = businessKey,
        };
        _db.EventSchedules.Add(schedule);
        return schedule;
    }

    private DateTimeOffset DueAt(ScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZone))
            throw new AgwException(ErrorCodes.InvalidParam, "Schedule time zone is required.");
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
            var now = _timeProvider.GetUtcNow();
            return request.Kind switch
            {
                ScheduleKind.Once => request.At ?? throw new AgwException(ErrorCodes.InvalidParam),
                ScheduleKind.Interval when request.Interval > TimeSpan.Zero => request.At
                    ?? now + request.Interval.Value,
                ScheduleKind.Cron when !string.IsNullOrWhiteSpace(request.Cron) => CronExpression
                    .Parse(request.Cron, CronFormat.Standard)
                    .GetNextOccurrence(now, timeZone)
                    ?? throw new AgwException(ErrorCodes.InvalidParam),
                _ => throw new AgwException(ErrorCodes.InvalidParam),
            };
        }
        catch (Exception exception)
            when (exception
                    is CronFormatException
                        or TimeZoneNotFoundException
                        or InvalidTimeZoneException
                        or ArgumentOutOfRangeException
            )
        {
            _logger.LogWarning(exception, "Event schedule configuration is invalid.");
            throw new AgwException(ErrorCodes.InvalidParam, "Schedule or time zone is invalid.", exception);
        }
    }

    private static ScheduleState State(EventSchedule row, bool accepted) =>
        new()
        {
            ScheduleId = row.Id,
            Status = (ScheduleStatus)row.State,
            DueAt = row.DueAt,
            RequestSeq = row.RequestSeq,
            Revision = row.Revision,
            Accepted = accepted,
        };

    private static void ValidateKey(string key, long seq)
    {
        if (string.IsNullOrWhiteSpace(key) || seq < 1)
            throw new AgwException(ErrorCodes.InvalidParam);
    }
}
