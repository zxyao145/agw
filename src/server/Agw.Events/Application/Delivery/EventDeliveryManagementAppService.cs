using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Observability;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Events.Application.Delivery;

public sealed class EventDeliveryManagementAppService
{
    private const string CancellationReason = "The event delivery was cancelled.";

    private readonly IEventsDbContext _db;
    private readonly IEventIngestTransaction _transaction;
    private readonly IOutboxStaging _outbox;
    private readonly IApplicationLock _applicationLock;
    private readonly IBackgroundExecutionFacade _executions;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventDeliveryManagementAppService(
        IEventsDbContext db,
        IEventIngestTransaction transaction,
        IOutboxStaging outbox,
        IApplicationLock applicationLock,
        IBackgroundExecutionFacade executions,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _transaction = transaction;
        _outbox = outbox;
        _applicationLock = applicationLock;
        _executions = executions;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<bool> RetryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        await using var lease = await _applicationLock
            .AcquireAsync($"events:delivery:{deliveryId:N}", cancellationToken)
            .ConfigureAwait(false);
        var delivery =
            await _db
                .EventDeliveries.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == deliveryId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.EventDeliveryNotFound);
        if (delivery.State != EventDeliveryState.Parked)
        {
            return false;
        }

        var eventRecord =
            await _db
                .Events.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == delivery.EventId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.EventDeliveryNotFound);

        var now = _timeProvider.GetUtcNow();
        var requeued = false;
        await _transaction
            .ExecuteAsync(
                async token =>
                {
                    lease.HandleLostToken.ThrowIfCancellationRequested();
                    var updated = await _db
                        .EventDeliveries.Where(row => row.Id == deliveryId && row.State == EventDeliveryState.Parked)
                        .ExecuteUpdateAsync(
                            setters =>
                                setters
                                    .SetProperty(row => row.State, EventDeliveryState.Pending)
                                    .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
                                    .SetProperty(row => row.LastError, (string?)null)
                                    .SetProperty(row => row.CompletedAt, (DateTimeOffset?)null)
                                    .SetProperty(row => row.RecoverAfter, now.Add(_options.Outbox.StaleAfter))
                                    .SetProperty(row => row.Version, row => row.Version + 1)
                                    .SetProperty(row => row.UpdateBy, ownerUserId)
                                    .SetProperty(row => row.UpdateTime, now),
                            token
                        )
                        .ConfigureAwait(false);
                    if (updated == 0)
                    {
                        return;
                    }

                    _outbox.Stage(
                        new OutboxMessage
                        {
                            Topic = EventTopics.Delivery,
                            EntityId = deliveryId,
                            Message = EventDeliveryMessageFactory.Create(eventRecord, deliveryId),
                        }
                    );
                    requeued = true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (requeued)
        {
            RecordTransition(EventDeliveryState.Pending);
            EventTelemetry.DeliveryRetries.Add(1);
        }
        return requeued;
    }

    public async Task<bool> CancelAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        await using var lease = await _applicationLock
            .AcquireAsync($"events:delivery:{deliveryId:N}", cancellationToken)
            .ConfigureAwait(false);
        var delivery =
            await _db
                .EventDeliveries.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == deliveryId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.EventDeliveryNotFound);

        if (delivery.State == EventDeliveryState.WaitingExecution)
        {
            if (delivery.TurnId is not { } turnId)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "The waiting event delivery has no TurnId.");
            }

            if (
                !await _executions
                    .CancelAsync(
                        new BackgroundExecutionCancelRequest(
                            turnId,
                            $"delivery:{deliveryId:D}",
                            delivery.EventId,
                            CancellationReason
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                return false;
            }
        }
        else if (
            delivery.State
            is not (EventDeliveryState.Pending or EventDeliveryState.RetryScheduled or EventDeliveryState.Held)
        )
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var cancelled = false;
        await _transaction
            .ExecuteAsync(
                async token =>
                {
                    lease.HandleLostToken.ThrowIfCancellationRequested();
                    var updated = await _db
                        .EventDeliveries.Where(row =>
                            row.Id == deliveryId
                            && row.State == delivery.State
                            && (!delivery.TurnId.HasValue || row.TurnId == delivery.TurnId)
                        )
                        .ExecuteUpdateAsync(
                            setters =>
                                setters
                                    .SetProperty(row => row.State, EventDeliveryState.Cancelled)
                                    .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
                                    .SetProperty(row => row.CompletedAt, now)
                                    .SetProperty(row => row.TurnId, (Guid?)null)
                                    .SetProperty(row => row.RequestSnapshotJson, (string?)null)
                                    .SetProperty(row => row.Version, row => row.Version + 1)
                                    .SetProperty(row => row.UpdateBy, ownerUserId)
                                    .SetProperty(row => row.UpdateTime, now),
                            token
                        )
                        .ConfigureAwait(false);
                    if (updated == 1)
                    {
                        if (delivery.State == EventDeliveryState.WaitingExecution)
                        {
                            await MarkAttemptCancelledAsync(delivery, now, token).ConfigureAwait(false);
                        }
                        await _outbox.CompleteAsync(EventTopics.Delivery, deliveryId, token).ConfigureAwait(false);
                        cancelled = true;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (cancelled)
        {
            RecordTransition(EventDeliveryState.Cancelled);
        }
        return cancelled;
    }

    private async Task MarkAttemptCancelledAsync(
        EventDelivery delivery,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken
    )
    {
        var updated = await _db
            .EventDeliveryAttempts.Where(row => row.DeliveryId == delivery.Id && row.Attempt == delivery.Attempt)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.EndedAt, endedAt)
                        .SetProperty(row => row.Result, EventDeliveryAttemptResult.Failed)
                        .SetProperty(row => row.Error, CancellationReason),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (updated != 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The event delivery attempt record is missing.");
        }
    }

    private static void RecordTransition(EventDeliveryState state) =>
        EventTelemetry.DeliveryTransitions.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
}
