using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Observability;
using Agw.Infrastructure.Data;
using Agw.Notifications.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventRecordRecovery
{
    private readonly AgwDbContext _db;
    private readonly IEventTransport _transport;
    private readonly IOutboxStaging _outbox;
    private readonly IEventIngestTransaction _transaction;
    private readonly IApplicationLock _applicationLock;
    private readonly INotificationDispatchStore _notificationDispatchStore;
    private readonly TimeProvider _timeProvider;
    private readonly EventsOptions _options;
    private readonly ILogger<EventRecordRecovery> _logger;

    public EventRecordRecovery(
        AgwDbContext db,
        IEventTransport transport,
        IOutboxStaging outbox,
        IEventIngestTransaction transaction,
        IApplicationLock applicationLock,
        INotificationDispatchStore notificationDispatchStore,
        TimeProvider timeProvider,
        IOptions<EventsOptions> options,
        ILogger<EventRecordRecovery> logger
    )
    {
        _db = db;
        _transport = transport;
        _outbox = outbox;
        _transaction = transaction;
        _applicationLock = applicationLock;
        _notificationDispatchStore = notificationDispatchStore;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> RecoverAsync(bool startup, CancellationToken cancellationToken)
    {
        await using var lease = await _applicationLock.AcquireAsync("events:unpublished-recovery", cancellationToken);
        var force = startup && _options.Transport.Provider.Equals("InProcess", StringComparison.OrdinalIgnoreCase);
        var now = _timeProvider.GetUtcNow();
        var recoveredRunning = await RecoverRunningDeliveriesAsync(now, force, lease.HandleLostToken, cancellationToken)
            .ConfigureAwait(false);
        if (_options.Outbox.Enabled)
        {
            EventTelemetry.Backlog.Record(
                await _db.EventOutbox.CountAsync(row => row.State == EventOutboxState.Pending, cancellationToken),
                new KeyValuePair<string, object?>("state", "Pending")
            );
            EventTelemetry.Backlog.Record(
                await _db.EventOutbox.CountAsync(row => row.State == EventOutboxState.Published, cancellationToken),
                new KeyValuePair<string, object?>("state", "Published")
            );
            var published = await RelayAsync(force, now, lease.HandleLostToken, cancellationToken);
            var recovered = published + recoveredRunning;
            EventTelemetry.Recovered.Record(recovered, new KeyValuePair<string, object?>("outbox", true));
            return recovered;
        }

        var count = recoveredRunning;
        var events = await _db
            .Events.IgnoreUserScope()
            .Where(row => row.DispatchState == EventDispatchState.Pending && (force || row.RecoverAfter <= now))
            .ToListAsync(cancellationToken);
        foreach (var record in events)
        {
            using var user = PushUser(record.CreateBy!);
            record.RecoverAfter = now.Add(_options.Outbox.StaleAfter);
            _outbox.Stage(
                new OutboxMessage
                {
                    Topic = EventMessages.Accepted,
                    EntityId = record.Id,
                    Message = EventMessages.FromRecord(record, "agweventid"),
                }
            );
            lease.HandleLostToken.ThrowIfCancellationRequested();
            await _db.SaveChangesAsync(cancellationToken);
            count++;
        }
        var deliveries = await _db
            .EventDeliveries.IgnoreUserScope()
            .Where(row => row.State == EventDeliveryState.Pending && (force || row.RecoverAfter <= now))
            .ToListAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            using var user = PushUser(delivery.CreateBy!);
            var record = await _db.Events.SingleAsync(row => row.Id == delivery.EventId, cancellationToken);
            delivery.RecoverAfter = now.Add(_options.Outbox.StaleAfter);
            _outbox.Stage(
                new OutboxMessage
                {
                    Topic = EventMessages.Delivery,
                    EntityId = delivery.Id,
                    Message = EventMessages.FromRecord(record, "agwdeliveryid", delivery.Id),
                }
            );
            lease.HandleLostToken.ThrowIfCancellationRequested();
            await _db.SaveChangesAsync(cancellationToken);
            count++;
        }
        var dispatches = await _db
            .NotificationDispatches.IgnoreUserScope()
            .Where(row =>
                row.ChannelKey != "desktop"
                && (
                    (
                        (
                            row.State == NotificationDispatchState.Pending
                            || row.State == NotificationDispatchState.UpdatePending
                        ) && (force || row.RecoverAfter <= now)
                    ) || (row.State == NotificationDispatchState.Sending && (force || row.ClaimExpiresAt <= now))
                )
            )
            .ToListAsync(cancellationToken);
        foreach (var dispatch in dispatches)
        {
            var notification = await _db
                .Notifications.IgnoreUserScope()
                .AsNoTracking()
                .SingleAsync(row => row.Id == dispatch.NotificationId, cancellationToken);
            using var user = PushUser(notification.CreateBy!);
            dispatch.RecoverAfter = now.Add(_options.Outbox.StaleAfter);
            if (dispatch.State == NotificationDispatchState.Sending)
            {
                dispatch.ClaimedBy = null;
                dispatch.ClaimAttemptId = null;
                if (force)
                    dispatch.ClaimExpiresAt = now;
            }
            _outbox.Stage(
                new OutboxMessage
                {
                    Topic = EventMessages.NotificationDispatch,
                    EntityId = dispatch.Id,
                    Message = NotificationMessage(dispatch),
                }
            );
            lease.HandleLostToken.ThrowIfCancellationRequested();
            await _db.SaveChangesAsync(cancellationToken);
            count++;
        }
        EventTelemetry.Recovered.Record(count, new KeyValuePair<string, object?>("outbox", false));
        EventTelemetry.Backlog.Record(events.Count, new("kind", "event"), new("outbox", false));
        EventTelemetry.Backlog.Record(deliveries.Count, new("kind", "delivery"), new("outbox", false));
        EventTelemetry.Backlog.Record(dispatches.Count, new("kind", "notification"), new("outbox", false));
        return count;
    }

    public async Task<int> RecoverDueRetriesAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _applicationLock.AcquireAsync("events:retry-recovery", cancellationToken);
        var now = _timeProvider.GetUtcNow();
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.HandleLostToken
        );
        var scanToken = leaseCancellation.Token;
        var deliveryIds = await RequeueDueDeliveriesAsync(now, lease.HandleLostToken, scanToken).ConfigureAwait(false);
        var dispatchCount = await _notificationDispatchStore.RecoverDueAsync(scanToken).ConfigureAwait(false);
        if (_options.Outbox.Enabled)
        {
            await RelayAsync(false, now, lease.HandleLostToken, scanToken, pendingOutboxOnly: true)
                .ConfigureAwait(false);
        }
        return deliveryIds.Count + dispatchCount;
    }

    private async Task<int> RecoverRunningDeliveriesAsync(
        DateTimeOffset now,
        bool force,
        CancellationToken leaseToken,
        CancellationToken cancellationToken
    )
    {
        DeliveryIdentity[] deliveries;
        deliveries = await _db
            .EventDeliveries.IgnoreUserScope()
            .AsNoTracking()
            .Where(row => row.State == EventDeliveryState.Running && (force || row.RecoverAfter <= now))
            .OrderBy(row => row.RecoverAfter)
            .Take(100)
            .Select(row => new DeliveryIdentity(row.Id, row.CreateBy!, row.EventId, row.Attempt, row.Version))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var recovered = 0;
        foreach (var identity in deliveries)
        {
            using var user = PushUser(identity.OwnerUserId);
            EventDeliveryState? resultingState = null;
            await _transaction
                .ExecuteAsync(
                    async token =>
                    {
                        leaseToken.ThrowIfCancellationRequested();
                        var terminalFailure = identity.Attempt >= _options.Delivery.MaxAttempts;
                        var state = terminalFailure ? EventDeliveryState.Parked : EventDeliveryState.RetryScheduled;
                        var nextAttemptAt = terminalFailure
                            ? (DateTimeOffset?)null
                            : now.Add(_options.Delivery.GetRetryDelay(identity.Attempt));
                        var error = "The event delivery attempt timed out.";
                        var updated = await _db
                            .EventDeliveries.Where(row =>
                                row.Id == identity.Id
                                && row.Attempt == identity.Attempt
                                && row.Version == identity.Version
                                && row.State == EventDeliveryState.Running
                                && (force || row.RecoverAfter <= now)
                            )
                            .ExecuteUpdateAsync(
                                setters =>
                                    setters
                                        .SetProperty(row => row.State, state)
                                        .SetProperty(row => row.NextAttemptAt, nextAttemptAt)
                                        .SetProperty(row => row.LastError, error)
                                        .SetProperty(row => row.CompletedAt, terminalFailure ? now : null)
                                        .SetProperty(row => row.UpdateBy, identity.OwnerUserId)
                                        .SetProperty(row => row.UpdateTime, now)
                                        .SetProperty(row => row.Version, row => row.Version + 1),
                                token
                            )
                            .ConfigureAwait(false);
                        if (updated == 0)
                        {
                            return;
                        }

                        var result = terminalFailure
                            ? EventDeliveryAttemptResult.Failed
                            : EventDeliveryAttemptResult.Retry;
                        var updatedAttempt = await _db
                            .EventDeliveryAttempts.Where(row =>
                                row.DeliveryId == identity.Id && row.Attempt == identity.Attempt
                            )
                            .ExecuteUpdateAsync(
                                setters =>
                                    setters
                                        .SetProperty(row => row.EndedAt, now)
                                        .SetProperty(row => row.Result, result)
                                        .SetProperty(row => row.Error, error),
                                token
                            )
                            .ConfigureAwait(false);
                        if (updatedAttempt != 1)
                        {
                            throw new AgwException(
                                ErrorCodes.InvalidParam,
                                "The event delivery attempt record is missing."
                            );
                        }
                        await _outbox.CompleteAsync(EventTopics.Delivery, identity.Id, token).ConfigureAwait(false);
                        resultingState = state;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (resultingState is { } state)
            {
                EventTelemetry.DeliveryTransitions.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
                if (state == EventDeliveryState.RetryScheduled)
                {
                    EventTelemetry.DeliveryRetries.Add(1);
                }
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<HashSet<Guid>> RequeueDueDeliveriesAsync(
        DateTimeOffset now,
        CancellationToken leaseToken,
        CancellationToken cancellationToken
    )
    {
        DeliveryIdentity[] deliveries;
        deliveries = await _db
            .EventDeliveries.IgnoreUserScope()
            .AsNoTracking()
            .Where(row => row.State == EventDeliveryState.RetryScheduled && row.NextAttemptAt <= now)
            .OrderBy(row => row.NextAttemptAt)
            .Take(100)
            .Select(row => new DeliveryIdentity(row.Id, row.CreateBy!, row.EventId, row.Attempt, row.Version))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var requeued = new HashSet<Guid>();
        foreach (var identity in deliveries)
        {
            using var user = PushUser(identity.OwnerUserId);
            var changed = false;
            await _transaction
                .ExecuteAsync(
                    async token =>
                    {
                        leaseToken.ThrowIfCancellationRequested();
                        var updated = await _db
                            .EventDeliveries.Where(row =>
                                row.Id == identity.Id
                                && row.Version == identity.Version
                                && row.State == EventDeliveryState.RetryScheduled
                                && row.NextAttemptAt <= now
                            )
                            .ExecuteUpdateAsync(
                                setters =>
                                    setters
                                        .SetProperty(row => row.State, EventDeliveryState.Pending)
                                        .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
                                        .SetProperty(row => row.LastError, (string?)null)
                                        .SetProperty(row => row.RecoverAfter, now.Add(_options.Outbox.StaleAfter))
                                        .SetProperty(row => row.UpdateBy, identity.OwnerUserId)
                                        .SetProperty(row => row.UpdateTime, now)
                                        .SetProperty(row => row.Version, row => row.Version + 1),
                                token
                            )
                            .ConfigureAwait(false);
                        if (updated == 0)
                        {
                            return;
                        }

                        var record = await _db
                            .Events.SingleAsync(row => row.Id == identity.EventId, token)
                            .ConfigureAwait(false);
                        _outbox.Stage(
                            new OutboxMessage
                            {
                                Topic = EventTopics.Delivery,
                                EntityId = identity.Id,
                                Message = EventMessages.FromRecord(record, "agwdeliveryid", identity.Id),
                            }
                        );
                        changed = true;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (changed)
            {
                EventTelemetry.DeliveryTransitions.Add(
                    1,
                    new KeyValuePair<string, object?>("state", EventDeliveryState.Pending.ToString())
                );
                requeued.Add(identity.Id);
            }
        }

        return requeued;
    }

    private async Task<int> RelayAsync(
        bool force,
        DateTimeOffset now,
        CancellationToken leaseToken,
        CancellationToken cancellationToken,
        bool pendingOutboxOnly = false
    )
    {
        var staleBefore = now.Subtract(_options.Outbox.StaleAfter);
        var query = _db.EventOutbox.AsNoTracking();
        if (pendingOutboxOnly)
        {
            query = query.Where(row => row.State == EventOutboxState.Pending);
        }
        else
        {
            query = query.Where(row =>
                row.State == EventOutboxState.Published && (force || row.PublishedAt <= staleBefore)
            );
        }
        IQueryable<EventOutbox> ordered = query.OrderBy(row => row.Id);
        if (pendingOutboxOnly)
            ordered = ordered.Take(100);
        var rows = await ordered.ToListAsync(cancellationToken);
        var count = 0;
        foreach (var row in rows)
        {
            try
            {
                var message = await ReadMessageAsync(row.Topic, row.EntityId, cancellationToken);
                leaseToken.ThrowIfCancellationRequested();
                if (message is null)
                {
                    await _db
                        .EventOutbox.Where(item => item.Id == row.Id && item.State != EventOutboxState.Completed)
                        .ExecuteUpdateAsync(
                            setters =>
                                setters
                                    .SetProperty(item => item.State, EventOutboxState.Completed)
                                    .SetProperty(item => item.CompletedAt, now),
                            cancellationToken
                        );
                    continue;
                }
                var messageId = await _transport.PublishAsync(row.Topic, message, cancellationToken);
                leaseToken.ThrowIfCancellationRequested();
                // 消费者可以在发布返回前完成处理。The consumer may complete before publication returns.
                await _db
                    .EventOutbox.Where(item => item.Id == row.Id && item.State != EventOutboxState.Completed)
                    .ExecuteUpdateAsync(
                        setters =>
                            setters
                                .SetProperty(item => item.State, EventOutboxState.Published)
                                .SetProperty(item => item.TransportMessageId, messageId)
                                .SetProperty(item => item.PublishedAt, now),
                        cancellationToken
                    );
                count++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Event outbox publication failed for {Topic} entity {EntityId}",
                    row.Topic,
                    row.EntityId
                );
            }
        }
        if (!pendingOutboxOnly)
        {
            await _db
                .EventOutbox.Where(row => row.State == EventOutboxState.Completed && row.CompletedAt < now.AddDays(-7))
                .ExecuteDeleteAsync(cancellationToken);
        }
        return count;
    }

    public async Task<CloudEventEnvelope?> ReadMessageAsync(
        string topic,
        Guid entityId,
        CancellationToken cancellationToken
    )
    {
        if (topic == EventMessages.Accepted)
        {
            var record = await _db
                .Events.IgnoreUserScope()
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == entityId, cancellationToken);
            return record is null || record.DispatchState != EventDispatchState.Pending
                ? null
                : EventMessages.FromRecord(record, "agweventid");
        }
        if (topic == EventMessages.Delivery)
        {
            var delivery = await _db
                .EventDeliveries.IgnoreUserScope()
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == entityId, cancellationToken);
            if (delivery is null || delivery.State is not (EventDeliveryState.Pending or EventDeliveryState.Running))
                return null;
            var record = await _db
                .Events.IgnoreUserScope()
                .AsNoTracking()
                .SingleAsync(row => row.Id == delivery.EventId, cancellationToken);
            return EventMessages.FromRecord(record, "agwdeliveryid", delivery.Id);
        }
        if (topic == EventMessages.NotificationDispatch)
        {
            var dispatch = await _db
                .NotificationDispatches.IgnoreUserScope()
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == entityId, cancellationToken);
            return
                dispatch is null
                || dispatch.State
                    is NotificationDispatchState.Sent
                        or NotificationDispatchState.Unsupported
                        or NotificationDispatchState.Parked
                ? null
                : NotificationMessage(dispatch);
        }
        throw new AgwException(ErrorCodes.InvalidParam, $"Unknown event outbox topic {topic}.");
    }

    public static CloudEventEnvelope NotificationMessage(NotificationDispatch dispatch)
    {
        var payload = JsonSerializer.SerializeToElement(
            new { dispatchId = dispatch.Id, messageVersion = dispatch.MessageVersion }
        );
        return new CloudEventEnvelope
        {
            Id = $"{dispatch.Id:D}:{dispatch.MessageVersion}",
            Source = "/agw/notifications",
            Type = "agw.notification.dispatch",
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = payload.GetRawText(),
                Json = payload,
            },
        };
    }

    private sealed record DeliveryIdentity
    {
        public DeliveryIdentity(Guid id, string ownerUserId, Guid eventId, int attempt, long version)
        {
            Id = id;
            OwnerUserId = ownerUserId;
            EventId = eventId;
            Attempt = attempt;
            Version = version;
        }

        public Guid Id { get; init; }

        public string OwnerUserId { get; init; }

        public Guid EventId { get; init; }

        public int Attempt { get; init; }

        public long Version { get; init; }
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "events-recovery"))
        );
}
