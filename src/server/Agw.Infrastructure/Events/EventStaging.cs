using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Observability;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventStaging : IEventStaging, IEventSourceFacade, IEventContextFacade
{
    private readonly AgwDbContext _db;
    private readonly IOutboxStaging _outbox;
    private readonly PendingTransportMessages _pending;
    private readonly IApplicationLock _applicationLock;
    private readonly CloudEventEnvelopeSerializer _serializer;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventStaging(
        AgwDbContext db,
        IOutboxStaging outbox,
        PendingTransportMessages pending,
        IApplicationLock applicationLock,
        CloudEventEnvelopeSerializer serializer,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _outbox = outbox;
        _pending = pending;
        _applicationLock = applicationLock;
        _serializer = serializer;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<EventPublishResult> StageAsync(EventStagingRequest request, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        ValidatePublisher(request.PublisherKey, request.Event.Type);
        await _pending.AcquireAsync(
            _applicationLock,
            $"events:publisher:{owner}:{request.PublisherKey}",
            cancellationToken
        );
        var source =
            _db.EventSources.Local.FirstOrDefault(row =>
                row.CreateBy == owner && row.PublisherKey == request.PublisherKey
            )
            ?? await _db.EventSources.SingleOrDefaultAsync(
                row => row.PublisherKey == request.PublisherKey,
                cancellationToken
            );
        if (source is null)
        {
            source = new EventSource
            {
                Id = Guid.CreateVersion7(),
                CreateBy = owner,
                Name = request.PublisherKey,
                Kind = EventSourceKind.Publisher,
                PublisherKey = request.PublisherKey,
                CeSource = PublisherSource(request.PublisherKey),
                ConfigJson = "{}",
                Enabled = true,
            };
            _db.EventSources.Add(source);
        }
        var draft = request.Event with { Source = request.Event.Source ?? source.CeSource };
        if (!BelongsToSource(draft.Source!, source.CeSource))
            throw new AgwException(ErrorCodes.InvalidParam, "CloudEvents source is outside the publisher source.");
        return await StageSourceAsync(source, draft, request, cancellationToken);
    }

    public async Task<EventPublishResult> StageSourceAsync(
        EventSource source,
        CloudEventDraft draft,
        EventStagingRequest? lineage,
        CancellationToken cancellationToken
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        if (source.CreateBy != owner)
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        var ceSource = draft.Source ?? source.CeSource;
        var duplicate =
            _db.Events.Local.FirstOrDefault(row =>
                row.SourceId == source.Id && row.CeSource == ceSource && row.CeId == draft.Id
            )
            ?? await _db.Events.SingleOrDefaultAsync(
                row => row.SourceId == source.Id && row.CeSource == ceSource && row.CeId == draft.Id,
                cancellationToken
            );
        if (duplicate is not null)
        {
            EventTelemetry.Received.Add(1, new("source_kind", source.Kind.ToString()), new("duplicate", true));
            return new EventPublishResult(duplicate.Id, true);
        }
        var parent = draft.ParentEventId is { } parentId
            ? await _db.Events.SingleOrDefaultAsync(row => row.Id == parentId, cancellationToken)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound)
            : null;
        var id = Guid.CreateVersion7();
        var record = new EventRecord
        {
            Id = id,
            CreateBy = owner,
            SourceId = source.Id,
            CeSource = ceSource,
            CeId = draft.Id,
            Type = draft.Type,
            CeTime = draft.Time,
            Subject = draft.Subject,
            CeDataContentType = draft.DataContentType,
            CeDataSchema = draft.DataSchema,
            CeData = CloudEventDataCodec.StoreData(draft.Data),
            CeDataIsBinary = draft.Data?.Kind == CloudEventDataKind.Binary,
            Scope = draft.Scope,
            TagsJson = draft.Tags is null ? null : JsonSerializer.Serialize(draft.Tags),
            Summary = draft.Summary,
            Link = draft.Link,
            ReceivedAt = _timeProvider.GetUtcNow(),
            ParentEventId = draft.ParentEventId,
            ChainId = parent?.ChainId ?? lineage?.ChainId ?? id,
            Hops = parent is null ? lineage?.Hops ?? 0 : parent.Hops + (lineage?.InheritParentHops == true ? 0 : 1),
            DispatchState = EventDispatchState.Pending,
            RecoverAfter = _timeProvider.GetUtcNow() + _options.Outbox.StaleAfter,
        };
        var message = EventMessages.FromRecord(record, "agweventid");
        _serializer.Validate(message);
        _db.Events.Add(record);
        EventTelemetry.Received.Add(1, new("source_kind", source.Kind.ToString()), new("duplicate", false));
        _outbox.Stage(
            new OutboxMessage
            {
                Topic = EventMessages.Accepted,
                EntityId = id,
                Message = message,
            }
        );
        return new EventPublishResult(id, false);
    }

    async Task<EventSourceIdentity?> IEventSourceFacade.GetAsync(Guid sourceId, CancellationToken cancellationToken)
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
                PublisherKey = source.PublisherKey,
                Source = source.CeSource,
            };
    }

    async Task<EventContext?> IEventContextFacade.GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var record = await _db.Events.AsNoTracking().SingleOrDefaultAsync(row => row.Id == eventId, cancellationToken);
        return record is null
            ? null
            : new EventContext
            {
                EventId = record.Id,
                SourceId = record.SourceId,
                ChainId = record.ChainId,
                ParentEventId = record.ParentEventId,
                Hops = record.Hops,
            };
    }

    private static bool BelongsToSource(string source, string prefix) =>
        source.Equals(prefix, StringComparison.Ordinal) || source.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static string PublisherSource(string key) =>
        key switch
        {
            "agw.execution" => "/agw/executions",
            "agw.notifications" => "/agw/notifications",
            _ when key.StartsWith("agw.scheduler:", StringComparison.Ordinal) => "/agw/schedules/" + key[14..],
            _ when key.StartsWith("ext:", StringComparison.Ordinal) => "/agw/extensions/" + key[4..],
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static void ValidatePublisher(string key, string eventType)
    {
        var host =
            key is "agw.execution" or "agw.notifications" || key.StartsWith("agw.scheduler:", StringComparison.Ordinal);
        if (!host && !key.StartsWith("ext:", StringComparison.Ordinal))
            throw new AgwException(ErrorCodes.InvalidParam);
        if (eventType.StartsWith("agw.", StringComparison.Ordinal) && !host)
            throw new AgwException(ErrorCodes.EventTypeReserved);
    }
}
