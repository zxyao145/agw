using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Observability;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Events.Application.Delivery;

public sealed class EventExecutionCompletionHandler : IEventExecutionCompletionHandler
{
    private const string CompletionEventType = "agw.execution.completed";
    private const string ExecutionPublisherKey = "agw.execution";

    private readonly IEventsDbContext _db;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventExecutionCompletionHandler(
        IEventsDbContext db,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task HandleAsync(EventRecord record, EventSource source, CancellationToken cancellationToken)
    {
        if (record.Type != CompletionEventType || source.PublisherKey != ExecutionPublisherKey || record.CeData is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(record.CeData);
        var data = document.RootElement;
        var caller = data.GetProperty("caller").GetString();
        if (caller is null || !caller.StartsWith("delivery:", StringComparison.Ordinal))
        {
            return;
        }

        if (
            !Guid.TryParse(caller.AsSpan("delivery:".Length), out var deliveryId)
            || data.GetProperty("correlationKey").GetString() != $"delivery:{deliveryId:D}"
            || !data.GetProperty("turnId").TryGetGuid(out var turnId)
        )
        {
            return;
        }

        var delivery = await _db
            .EventDeliveries.SingleOrDefaultAsync(
                row => row.Id == deliveryId && row.TurnId == turnId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            delivery is null
            || delivery.State
                is not (
                    EventDeliveryState.Pending
                    or EventDeliveryState.Running
                    or EventDeliveryState.WaitingExecution
                    or EventDeliveryState.RetryScheduled
                    or EventDeliveryState.Parked
                )
        )
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var state = data.GetProperty("state").GetString();
        var succeeded = state == AgentExecutionState.Completed.ToString();
        var error =
            data.GetProperty("error").ValueKind == JsonValueKind.String ? data.GetProperty("error").GetString() : null;
        var terminalFailure = !succeeded && delivery.Attempt >= _options.Delivery.MaxAttempts;

        delivery.State =
            succeeded ? EventDeliveryState.Succeeded
            : terminalFailure ? EventDeliveryState.Parked
            : EventDeliveryState.RetryScheduled;
        delivery.NextAttemptAt =
            delivery.State == EventDeliveryState.RetryScheduled
                ? now.Add(_options.Delivery.GetRetryDelay(delivery.Attempt))
                : null;
        delivery.LastError = succeeded ? null : error ?? $"Background execution ended in {state}.";
        delivery.CompletedAt = succeeded ? now : null;
        if (!succeeded)
        {
            delivery.TurnId = null;
            delivery.RequestSnapshotJson = null;
        }
        delivery.UpdateBy = record.CreateBy;
        delivery.UpdateTime = now;
        delivery.Version++;

        var attempt = await _db
            .EventDeliveryAttempts.SingleOrDefaultAsync(
                row => row.DeliveryId == deliveryId && row.Attempt == delivery.Attempt,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (attempt is not null)
        {
            attempt.EndedAt = now;
            attempt.Result =
                succeeded ? EventDeliveryAttemptResult.Succeeded
                : terminalFailure ? EventDeliveryAttemptResult.Failed
                : EventDeliveryAttemptResult.Retry;
            attempt.Error = delivery.LastError;
            EventTelemetry.DeliveryDuration.Record(
                Math.Max(0, (now - attempt.StartedAt).TotalSeconds),
                new KeyValuePair<string, object?>("state", delivery.State.ToString())
            );
        }

        EventTelemetry.DeliveryTransitions.Add(
            1,
            new KeyValuePair<string, object?>("state", delivery.State.ToString())
        );
        if (delivery.State == EventDeliveryState.RetryScheduled)
        {
            EventTelemetry.DeliveryRetries.Add(1);
        }
    }
}
