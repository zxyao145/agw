using System.Text.Json;
using Agw.Events.Application.Delivery;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Fact]
    public async Task HandleAsync_CompletedBackgroundExecution_SucceedsDelivery()
    {
        var ownerUserId = "delivery-completion-owner";
        using var user = PushUser(ownerUserId);
        await using var host = await StagingHost.CreateAsync(outbox: false);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>();
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow();
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = ownerUserId,
                EventId = Guid.CreateVersion7(),
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.WaitingExecution,
                Attempt = 1,
                TurnId = turnId,
                RecoverAfter = startedAt,
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                HostId = "events-test-host",
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var eventData = JsonSerializer.SerializeToElement(
            new
            {
                turnId,
                caller = $"delivery:{deliveryId:D}",
                correlationKey = $"delivery:{deliveryId:D}",
                state = "Completed",
                error = (string?)null,
            }
        );
        var staged = await scope
            .ServiceProvider.GetRequiredService<IEventStaging>()
            .StageAsync(
                new EventStagingRequest
                {
                    PublisherKey = "agw.execution",
                    Event = new CloudEventDraft
                    {
                        Id = $"{turnId:D}:completed",
                        Type = "agw.execution.completed",
                        Source = "/agw/executions",
                        DataContentType = "application/json",
                        Data = new CloudEventData
                        {
                            Kind = CloudEventDataKind.Json,
                            Text = eventData.GetRawText(),
                            Json = eventData,
                        },
                    },
                },
                TestContext.Current.CancellationToken
            );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var eventRecord = await db.Events.SingleAsync(
            row => row.Id == staged.EventId,
            TestContext.Current.CancellationToken
        );
        var eventSource = await db.EventSources.SingleAsync(
            row => row.Id == eventRecord.SourceId,
            TestContext.Current.CancellationToken
        );
        var handler = new EventExecutionCompletionHandler(
            db,
            Options.Create(new Agw.Events.Configuration.EventsOptions()),
            TimeProvider.System
        );

        await handler.HandleAsync(eventRecord, eventSource, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            EventDeliveryState.Succeeded,
            (
                await db.EventDeliveries.SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken)
            ).State
        );
        Assert.Equal(
            EventDeliveryAttemptResult.Succeeded,
            (
                await db.EventDeliveryAttempts.SingleAsync(
                    row => row.DeliveryId == deliveryId,
                    TestContext.Current.CancellationToken
                )
            ).Result
        );
    }

    [Fact]
    public async Task HandleAsync_FailedBackgroundExecution_SchedulesRetry()
    {
        var ownerUserId = "delivery-retry-owner";
        using var user = PushUser(ownerUserId);
        await using var host = await StagingHost.CreateAsync(outbox: false);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>();
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow();
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = ownerUserId,
                EventId = Guid.CreateVersion7(),
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.WaitingExecution,
                Attempt = 1,
                TurnId = turnId,
                RecoverAfter = startedAt,
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                HostId = "events-test-host",
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var eventData = JsonSerializer.SerializeToElement(
            new
            {
                turnId,
                caller = $"delivery:{deliveryId:D}",
                correlationKey = $"delivery:{deliveryId:D}",
                state = "Failed",
                error = "execution failed",
            }
        );
        var staged = await scope
            .ServiceProvider.GetRequiredService<IEventStaging>()
            .StageAsync(
                new EventStagingRequest
                {
                    PublisherKey = "agw.execution",
                    Event = new CloudEventDraft
                    {
                        Id = $"{turnId:D}:completed",
                        Type = "agw.execution.completed",
                        Source = "/agw/executions",
                        DataContentType = "application/json",
                        Data = new CloudEventData
                        {
                            Kind = CloudEventDataKind.Json,
                            Text = eventData.GetRawText(),
                            Json = eventData,
                        },
                    },
                },
                TestContext.Current.CancellationToken
            );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var eventRecord = await db.Events.SingleAsync(
            row => row.Id == staged.EventId,
            TestContext.Current.CancellationToken
        );
        var eventSource = await db.EventSources.SingleAsync(
            row => row.Id == eventRecord.SourceId,
            TestContext.Current.CancellationToken
        );
        var options = new Agw.Events.Configuration.EventsOptions();
        options.Delivery.BaseBackoff = TimeSpan.FromSeconds(10);
        options.Delivery.MaxBackoff = TimeSpan.FromMinutes(1);
        options.Delivery.MaxAttempts = 3;
        var handler = new EventExecutionCompletionHandler(db, Options.Create(options), TimeProvider.System);

        await handler.HandleAsync(eventRecord, eventSource, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var delivery = await db.EventDeliveries.SingleAsync(
            row => row.Id == deliveryId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(EventDeliveryState.RetryScheduled, delivery.State);
        Assert.Equal("execution failed", delivery.LastError);
        Assert.True(delivery.NextAttemptAt > startedAt);
        Assert.Equal(
            EventDeliveryAttemptResult.Retry,
            (
                await db.EventDeliveryAttempts.SingleAsync(
                    row => row.DeliveryId == deliveryId,
                    TestContext.Current.CancellationToken
                )
            ).Result
        );
    }
}
