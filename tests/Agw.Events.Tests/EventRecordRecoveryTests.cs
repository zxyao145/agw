using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Fact]
    public async Task RecoverDueRetriesAsync_EnabledOutbox_PublishesPendingMessageAndPreservesCompletion()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        Guid eventId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            eventId = (
                await scope
                    .ServiceProvider.GetRequiredService<IEventStaging>()
                    .StageAsync(Request("recover-outbox"), TestContext.Current.CancellationToken)
            ).EventId;
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
            await scope
                .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("recover-outbox", (await host.ReadAcceptedAsync()).Message.Id);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            Assert.Equal(
                EventOutboxState.Published,
                (await db.EventOutbox.SingleAsync(TestContext.Current.CancellationToken)).State
            );
            await scope
                .ServiceProvider.GetRequiredService<IOutboxStaging>()
                .CompleteAsync(EventMessages.Accepted, eventId, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Equal(
                0,
                await scope
                    .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                    .RecoverAsync(true, TestContext.Current.CancellationToken)
            );
    }

    [Fact]
    public async Task RecoverAsync_InProcessStartup_IgnoresFutureRecoveryTime()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(false);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            db.EventSources.Add(
                new EventSource
                {
                    Id = Guid.CreateVersion7(),
                    CreateBy = "owner",
                    Name = "source",
                    CeSource = "/source",
                }
            );
            db.Events.Add(
                new EventRecord
                {
                    Id = Guid.CreateVersion7(),
                    CreateBy = "owner",
                    CeId = "recover-direct",
                    CeSource = "/source",
                    Type = "example.updated",
                    RecoverAfter = TimeProvider.System.GetUtcNow().AddDays(1),
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Equal(
                0,
                await scope
                    .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                    .RecoverAsync(false, TestContext.Current.CancellationToken)
            );
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Equal(
                1,
                await scope
                    .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                    .RecoverAsync(true, TestContext.Current.CancellationToken)
            );
        Assert.Equal("recover-direct", (await host.ReadAcceptedAsync()).Message.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverDueRetriesAsync_DueRetries_RequeuesAndPublishes(bool outbox)
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(outbox);
        var now = TimeProvider.System.GetUtcNow();
        Guid deliveryId;
        Guid dispatchId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var staging = services.GetRequiredService<IEventStaging>();
            var db = services.GetRequiredService<AgwDbContext>();
            var eventId = (
                await staging.StageAsync(Request("retry-scan"), TestContext.Current.CancellationToken)
            ).EventId;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
            record.DispatchState = EventDispatchState.Dispatched;
            deliveryId = Guid.CreateVersion7();
            db.EventDeliveries.Add(
                new EventDelivery
                {
                    Id = deliveryId,
                    CreateBy = "owner",
                    EventId = eventId,
                    SubscriptionId = Guid.CreateVersion7(),
                    State = EventDeliveryState.RetryScheduled,
                    Attempt = 1,
                    NextAttemptAt = now.AddSeconds(-1),
                    RecoverAfter = now.AddSeconds(-1),
                    LastError = "delivery attempt failed",
                }
            );
            var notificationId = Guid.CreateVersion7();
            db.Notifications.Add(
                new Notification
                {
                    Id = notificationId,
                    CreateBy = "owner",
                    Title = "Retry notification",
                    Text = "Notification text",
                    CallerKey = "retry-test",
                    IdempotencyKey = "retry-scan",
                    CorrelationKey = "retry-scan",
                    ChangedAt = now,
                }
            );
            dispatchId = Guid.CreateVersion7();
            db.NotificationDispatches.Add(
                new NotificationDispatch
                {
                    Id = dispatchId,
                    NotificationId = notificationId,
                    ChannelKey = "test-channel",
                    State = NotificationDispatchState.Failed,
                    MessageVersion = 1,
                    Attempt = 1,
                    NextAttemptAt = now.AddSeconds(-1),
                    RecoverAfter = now.AddSeconds(-1),
                    LastError = "notification attempt failed",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), TimeProvider.System);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                TestContext.Current.CancellationToken
            );
            await using var staleRecoveryLease = await services
                .GetRequiredService<IApplicationLock>()
                .AcquireAsync("events:unpublished-recovery", cancellation.Token);
            await services.GetRequiredService<EventRecordRecovery>().RecoverDueRetriesAsync(cancellation.Token);
            var db = services.GetRequiredService<AgwDbContext>();
            db.ChangeTracker.Clear();
            var delivery = await db
                .EventDeliveries.AsNoTracking()
                .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
            var dispatch = await db
                .NotificationDispatches.AsNoTracking()
                .SingleAsync(row => row.Id == dispatchId, TestContext.Current.CancellationToken);
            Assert.Equal(EventDeliveryState.Pending, delivery.State);
            Assert.Null(delivery.NextAttemptAt);
            Assert.Null(delivery.LastError);
            Assert.True(delivery.RecoverAfter > now);
            Assert.Equal(NotificationDispatchState.Pending, dispatch.State);
            Assert.Null(dispatch.NextAttemptAt);
            Assert.Equal("notification attempt failed", dispatch.LastError);
            Assert.True(dispatch.RecoverAfter > now);
        }

        var deliveryMessage = await host.ReadTransportDeliveryAsync(EventTopics.Delivery);
        Assert.Equal(deliveryId.ToString("D"), deliveryMessage.Message.Extensions["agwdeliveryid"].Value);
        var dispatchMessage = await host.ReadTransportDeliveryAsync(EventTopics.NotificationDispatch);
        Assert.Contains(dispatchId.ToString("D"), dispatchMessage.Message.Id);
    }
}
