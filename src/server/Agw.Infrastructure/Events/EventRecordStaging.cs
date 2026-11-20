using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Observability;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventRecordStaging : IEventRecordStaging
{
    private readonly AgwDbContext _db;
    private readonly IOutboxStaging _outbox;
    private readonly CloudEventEnvelopeSerializer _serializer;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventRecordStaging(
        AgwDbContext db,
        IOutboxStaging outbox,
        CloudEventEnvelopeSerializer serializer,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _outbox = outbox;
        _serializer = serializer;
        _options = options.Value;
        _timeProvider = timeProvider;
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
}
