using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventRecoveryControl : IEventRecoveryControl
{
    private readonly AgwDbContext _db;
    private readonly EventRecordRecovery _recovery;
    private readonly IEventTransport _transport;
    private readonly IOutboxStaging _outbox;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventRecoveryControl(
        AgwDbContext db,
        EventRecordRecovery recovery,
        IEventTransport transport,
        IOutboxStaging outbox,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _recovery = recovery;
        _transport = transport;
        _outbox = outbox;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public Task<PagedResult<UnpublishedEventRecord>> ListAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var events = _db
            .Events.AsNoTracking()
            .Where(row => row.DispatchState == EventDispatchState.Pending && row.RecoverAfter <= now)
            .Select(row => new WaitingRow
            {
                Topic = EventMessages.Accepted,
                EntityId = row.Id,
                NotificationState = null,
                RecoverAfter = row.RecoverAfter,
            });
        var deliveries = _db
            .EventDeliveries.AsNoTracking()
            .Where(row => row.State == EventDeliveryState.Pending && row.RecoverAfter <= now)
            .Select(row => new WaitingRow
            {
                Topic = EventMessages.Delivery,
                EntityId = row.Id,
                NotificationState = null,
                RecoverAfter = row.RecoverAfter,
            });
        var notifications = _db
            .NotificationDispatches.AsNoTracking()
            .Where(row =>
                row.ChannelKey != "desktop"
                && (
                    (
                        (
                            row.State == NotificationDispatchState.Pending
                            || row.State == NotificationDispatchState.UpdatePending
                        )
                        && row.RecoverAfter <= now
                    ) || (row.State == NotificationDispatchState.Sending && row.ClaimExpiresAt <= now)
                )
            )
            .Select(row => new WaitingRow
            {
                Topic = EventMessages.NotificationDispatch,
                EntityId = row.Id,
                NotificationState = row.State,
                RecoverAfter = row.RecoverAfter,
            });

        var rows = events
            .Concat(deliveries)
            .Concat(notifications)
            .OrderBy(row => row.RecoverAfter)
            .ThenBy(row => row.EntityId);
        return EventPaging.ToPagedResultAsync(rows, pageIndex, pageSize, ToUnpublishedRecord, cancellationToken);
    }

    private static UnpublishedEventRecord ToUnpublishedRecord(WaitingRow row) =>
        new()
        {
            Topic = row.Topic,
            EntityId = row.EntityId,
            State = row.NotificationState?.ToString() ?? nameof(EventDispatchState.Pending),
            RecoverAfter = row.RecoverAfter,
        };

    public async Task RepublishAsync(string topic, Guid entityId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var recoverAfter = now.Add(_options.Outbox.StaleAfter);
        switch (topic)
        {
            case EventMessages.Accepted:
                var record =
                    await _db.Events.SingleOrDefaultAsync(
                        row => row.Id == entityId && row.DispatchState == EventDispatchState.Pending,
                        cancellationToken
                    ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);
                record.RecoverAfter = recoverAfter;
                break;
            case EventMessages.Delivery:
                var delivery =
                    await _db.EventDeliveries.SingleOrDefaultAsync(
                        row => row.Id == entityId && row.State == EventDeliveryState.Pending,
                        cancellationToken
                    ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);
                delivery.RecoverAfter = recoverAfter;
                break;
            case EventMessages.NotificationDispatch:
                var dispatch =
                    await _db.NotificationDispatches.SingleOrDefaultAsync(
                        row =>
                            row.Id == entityId
                            && row.ChannelKey != "desktop"
                            && (
                                row.State == NotificationDispatchState.Pending
                                || row.State == NotificationDispatchState.UpdatePending
                                || row.State == NotificationDispatchState.Sending && row.ClaimExpiresAt <= now
                            ),
                        cancellationToken
                    ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);
                dispatch.RecoverAfter = recoverAfter;
                break;
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }
        var message =
            await _recovery.ReadMessageAsync(topic, entityId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        if (_options.Outbox.Enabled)
            _outbox.Stage(
                new OutboxMessage
                {
                    Topic = topic,
                    EntityId = entityId,
                    Message = message,
                }
            );
        await _db.SaveChangesAsync(cancellationToken);
        var messageId = await _transport.PublishAsync(topic, message, cancellationToken);
        if (_options.Outbox.Enabled)
            await _db
                .EventOutbox.Where(row =>
                    row.Topic == topic && row.EntityId == entityId && row.State != EventOutboxState.Completed
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(row => row.State, EventOutboxState.Published)
                            .SetProperty(row => row.TransportMessageId, messageId)
                            .SetProperty(row => row.PublishedAt, now),
                    cancellationToken
                );
    }

    private sealed record WaitingRow
    {
        public required string Topic { get; init; }
        public required Guid EntityId { get; init; }
        public required NotificationDispatchState? NotificationState { get; init; }
        public required DateTimeOffset RecoverAfter { get; init; }
    }
}
