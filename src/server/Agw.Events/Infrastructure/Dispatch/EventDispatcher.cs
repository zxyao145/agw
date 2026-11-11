using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Matching;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Events.Infrastructure.Dispatch;

public sealed class EventDispatcher
{
    private readonly IEventsDbContext _db;
    private readonly IEventIngestTransaction _transaction;
    private readonly IOutboxStaging _outbox;
    private readonly IApplicationLock _applicationLock;
    private readonly IEventDispatchNoticeStaging _notices;
    private readonly IEnumerable<IEventExecutionCompletionHandler> _completionHandlers;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(
        IEventsDbContext db,
        IEventIngestTransaction transaction,
        IOutboxStaging outbox,
        IApplicationLock applicationLock,
        IEventDispatchNoticeStaging notices,
        IEnumerable<IEventExecutionCompletionHandler> completionHandlers,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider,
        ILogger<EventDispatcher> logger
    )
    {
        _db = db;
        _transaction = transaction;
        _outbox = outbox;
        _applicationLock = applicationLock;
        _notices = notices;
        _completionHandlers = completionHandlers;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task DispatchAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        var message = delivery.Message;
        if (
            !message.Extensions.TryGetValue("agweventid", out var locator)
            || !Guid.TryParse(locator.Value, out var eventId)
        )
        {
            _logger.LogError("Accepted event message {MessageId} has no valid event locator", delivery.MessageId);
            return;
        }
        await using var lease = await _applicationLock.AcquireAsync($"events:dispatch:{eventId}", cancellationToken);
        EventRecord? record;
        using (UserInfoUtil.PushSystemScope())
            record = await _db.Events.SingleOrDefaultAsync(row => row.Id == eventId, cancellationToken);
        if (record is null || message.Source != record.CeSource || message.Id != record.CeId)
        {
            _logger.LogError(
                "Accepted event message {MessageId} does not match event {EventId}",
                delivery.MessageId,
                eventId
            );
            return;
        }
        using var user = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, record.CreateBy!)], "event-dispatch")
            )
        );
        if (record.DispatchState != EventDispatchState.Pending)
            return;
        var source = await _db.EventSources.SingleAsync(row => row.Id == record.SourceId, cancellationToken);
        await _transaction.ExecuteAsync(
            async token =>
            {
                if (record.Hops > _options.Dispatch.MaxHops)
                {
                    record.DispatchState = EventDispatchState.HopLimitExceeded;
                    await _notices.StageHopLimitAsync(record, token);
                }
                else
                {
                    foreach (var handler in _completionHandlers)
                        await handler.HandleAsync(record, source, token);
                    var subscriptions = await _db.EventSubscriptions.Where(row => row.Enabled).ToListAsync(token);
                    var matches = EventSubscriptionMatcher.Match(
                        MatchRecord(record, message),
                        source,
                        subscriptions,
                        (subscription, error) =>
                            _logger.LogError(
                                "Event subscription {SubscriptionId} JsonLogic matching failed: {Error}",
                                subscription.Id,
                                error
                            )
                    );
                    foreach (var subscription in matches)
                    {
                        var target = new EventDelivery
                        {
                            Id = Guid.CreateVersion7(),
                            CreateBy = record.CreateBy,
                            EventId = record.Id,
                            SubscriptionId = subscription.Id,
                            State = EventDeliveryState.Pending,
                            RecoverAfter = _timeProvider.GetUtcNow().Add(_options.Outbox.StaleAfter),
                        };
                        _db.EventDeliveries.Add(target);
                        var extensions = message
                            .Extensions.Where(pair => pair.Key != "agweventid")
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        extensions["agwdeliveryid"] = new(CloudEventAttributeKind.String, target.Id.ToString());
                        _outbox.Stage(
                            new OutboxMessage
                            {
                                Topic = EventTopics.Delivery,
                                EntityId = target.Id,
                                Message = message with { Extensions = extensions },
                            }
                        );
                    }
                    record.MatchedCount = matches.Count;
                    record.DispatchState =
                        matches.Count == 0 ? EventDispatchState.NoMatch : EventDispatchState.Dispatched;
                }
                await _outbox.CompleteAsync(EventTopics.Accepted, eventId, token);
                lease.HandleLostToken.ThrowIfCancellationRequested();
            },
            cancellationToken
        );
    }

    private static EventRecord MatchRecord(EventRecord record, CloudEventEnvelope message) =>
        new()
        {
            Id = record.Id,
            SourceId = record.SourceId,
            CreateBy = record.CreateBy,
            CeSource = message.Source,
            CeId = message.Id,
            Type = message.Type,
            CeTime = message.Time,
            Subject = message.Subject,
            CeDataContentType = message.DataContentType,
            CeDataSchema = message.DataSchema,
            CeData = message.Data?.Kind == CloudEventDataKind.Binary ? null : message.Data?.Text,
            CeDataIsBinary = message.Data?.Kind == CloudEventDataKind.Binary,
            Scope = message.Extensions.TryGetValue("scope", out var scope) ? scope.Value : null,
            TagsJson = message.Extensions.TryGetValue("tags", out var tags)
                ? JsonSerializer.Serialize(tags.Value.Split(','))
                : "[]",
        };
}
