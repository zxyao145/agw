using Agw.Events.Contracts;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchAsync_RepeatedMessage_CreatesOneDeliveryAndCompletesAccepted(bool outbox)
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(outbox);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var eventId = (
            await scope
                .ServiceProvider.GetRequiredService<IEventStaging>()
                .StageAsync(Request("dispatch"), TestContext.Current.CancellationToken)
        ).EventId;
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = Guid.CreateVersion7(),
                CreateBy = "owner",
                Name = "subscription",
                Enabled = true,
                EventTypesJson = "[\"example.*\"]",
                TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(TestContext.Current.CancellationToken);
        var message = new Agw.Events.Contracts.Transport.TransportDelivery
        {
            Topic = EventMessages.Accepted,
            MessageId = "accepted",
            DeliveryCount = 1,
            Message = EventMessages.FromRecord(record, "agweventid"),
        };
        var dispatcher = scope.ServiceProvider.GetRequiredService<EventDispatcher>();
        await dispatcher.DispatchAsync(message, TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(EventDispatchState.Dispatched, record.DispatchState);
        Assert.Equal(1, record.MatchedCount);
        Assert.Single(await db.EventDeliveries.ToArrayAsync(TestContext.Current.CancellationToken));
        if (outbox)
            Assert.Equal(
                EventOutboxState.Completed,
                (
                    await db.EventOutbox.SingleAsync(
                        row => row.EntityId == eventId,
                        TestContext.Current.CancellationToken
                    )
                ).State
            );
        else
            Assert.Empty(await db.EventOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DispatchAsync_HopLimit_StagesOwnerNoticeWithFinalState()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await scope
            .ServiceProvider.GetRequiredService<IEventStaging>()
            .StageAsync(Request("too-deep") with { Hops = 9 }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(TestContext.Current.CancellationToken);
        await scope
            .ServiceProvider.GetRequiredService<EventDispatcher>()
            .DispatchAsync(
                new()
                {
                    Topic = EventMessages.Accepted,
                    MessageId = "accepted",
                    DeliveryCount = 1,
                    Message = EventMessages.FromRecord(record, "agweventid"),
                },
                TestContext.Current.CancellationToken
            );

        Assert.Equal(EventDispatchState.HopLimitExceeded, record.DispatchState);
        Assert.Empty(await db.EventDeliveries.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("owner", (await db.Notifications.SingleAsync(TestContext.Current.CancellationToken)).CreateBy);
        Assert.Equal(
            EventOutboxState.Completed,
            (
                await db.EventOutbox.SingleAsync(
                    row => row.EntityId == record.Id,
                    TestContext.Current.CancellationToken
                )
            ).State
        );
    }
}
