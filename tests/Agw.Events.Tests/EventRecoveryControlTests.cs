using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Tests;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed class EventRecoveryControlTests
{
    [Fact]
    public async Task ListAsync_WaitingRecords_PagesAcrossRecordKinds()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await WaitingRecordsScenarioAsync(host, TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ListAsync_WaitingRecordsOnPostgres_PagesAcrossRecordKinds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgresEventTestDatabase.CreateAsync(cancellationToken);
        await using var host = await ModuleCompositionTestHost.CreateAsync(settings: database.HostSettings);
        await WaitingRecordsScenarioAsync(host, cancellationToken);
    }

    private static async Task WaitingRecordsScenarioAsync(
        ModuleCompositionTestHost host,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        using (PushUser("other"))
        {
            await using var otherScope = host.Services.CreateAsyncScope();
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            otherDb.Events.Add(NewEvent(now.AddMinutes(-15)));
            await otherDb.SaveChangesAsync(cancellationToken);
        }

        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var expected = await SeedWaitingRecordsAsync(db, now, cancellationToken);
        var recovery = scope.ServiceProvider.GetRequiredService<IEventRecoveryControl>();
        var first = await recovery.ListAsync(1, 10, cancellationToken);
        var second = await recovery.ListAsync(2, 10, cancellationToken);

        Assert.Equal(12, first.Total);
        Assert.Equal(12, second.Total);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(expected, first.Items.Concat(second.Items).ToArray());
    }

    private static async Task<UnpublishedEventRecord[]> SeedWaitingRecordsAsync(
        AgwDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var events = new[] { -12, -9, -6, -2 }.Select(minutes => NewEvent(now.AddMinutes(minutes))).ToArray();
        var eventId = events[0].Id;
        var deliveries = new[] { -11, -8, -5, -2 }
            .Select(minutes => NewDelivery(eventId, now.AddMinutes(minutes)))
            .ToArray();
        events[^1].Id = Guid.Parse("019a4c17-0000-7000-8000-000000000002");
        deliveries[^1].Id = Guid.Parse("019a4c17-0000-7000-8000-000000000001");
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            Title = "Waiting records",
            CallerKey = "paging",
            IdempotencyKey = "waiting",
            ChangedAt = now,
        };
        NotificationDispatch[] dispatches =
        [
            NewDispatch(notification.Id, "pending-a", NotificationDispatchState.Pending, now.AddMinutes(-10)),
            NewDispatch(notification.Id, "pending-b", NotificationDispatchState.Pending, now.AddMinutes(-7)),
            NewDispatch(notification.Id, "update", NotificationDispatchState.UpdatePending, now.AddMinutes(-4)),
            NewDispatch(
                notification.Id,
                "expired",
                NotificationDispatchState.Sending,
                now.AddMinutes(-1),
                now.AddSeconds(-1)
            ),
        ];
        db.Events.AddRange(events);
        db.Events.Add(NewEvent(now.AddMinutes(15)));
        db.EventDeliveries.AddRange(deliveries);
        db.EventDeliveries.Add(NewDelivery(eventId, now.AddMinutes(-15), EventDeliveryState.Succeeded));
        db.Notifications.Add(notification);
        db.NotificationDispatches.AddRange(dispatches);
        db.NotificationDispatches.AddRange(
            NewDispatch(notification.Id, "desktop", NotificationDispatchState.Pending, now.AddMinutes(-15)),
            NewDispatch(
                notification.Id,
                "claimed",
                NotificationDispatchState.Sending,
                now.AddMinutes(-15),
                now.AddHours(1)
            )
        );
        await db.SaveChangesAsync(cancellationToken);

        return
        [
            Expected(EventMessages.Accepted, events[0].Id, "Pending", now.AddMinutes(-12)),
            Expected(EventMessages.Delivery, deliveries[0].Id, "Pending", now.AddMinutes(-11)),
            Expected(EventMessages.NotificationDispatch, dispatches[0].Id, "Pending", now.AddMinutes(-10)),
            Expected(EventMessages.Accepted, events[1].Id, "Pending", now.AddMinutes(-9)),
            Expected(EventMessages.Delivery, deliveries[1].Id, "Pending", now.AddMinutes(-8)),
            Expected(EventMessages.NotificationDispatch, dispatches[1].Id, "Pending", now.AddMinutes(-7)),
            Expected(EventMessages.Accepted, events[2].Id, "Pending", now.AddMinutes(-6)),
            Expected(EventMessages.Delivery, deliveries[2].Id, "Pending", now.AddMinutes(-5)),
            Expected(EventMessages.NotificationDispatch, dispatches[2].Id, "UpdatePending", now.AddMinutes(-4)),
            Expected(EventMessages.Delivery, deliveries[3].Id, "Pending", now.AddMinutes(-2)),
            Expected(EventMessages.Accepted, events[3].Id, "Pending", now.AddMinutes(-2)),
            Expected(EventMessages.NotificationDispatch, dispatches[3].Id, "Sending", now.AddMinutes(-1)),
        ];
    }

    private static UnpublishedEventRecord Expected(string topic, Guid id, string state, DateTimeOffset recoverAfter) =>
        new()
        {
            Topic = topic,
            EntityId = id,
            State = state,
            RecoverAfter = recoverAfter,
        };

    private static EventRecord NewEvent(DateTimeOffset recoverAfter)
    {
        var id = Guid.CreateVersion7();
        return new EventRecord
        {
            Id = id,
            CeId = id.ToString("N"),
            CeSource = "/paging",
            Type = "paging.test",
            DispatchState = EventDispatchState.Pending,
            RecoverAfter = recoverAfter,
        };
    }

    private static EventDelivery NewDelivery(
        Guid eventId,
        DateTimeOffset recoverAfter,
        EventDeliveryState state = EventDeliveryState.Pending
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EventId = eventId,
            SubscriptionId = Guid.CreateVersion7(),
            State = state,
            RecoverAfter = recoverAfter,
        };

    private static NotificationDispatch NewDispatch(
        Guid notificationId,
        string channel,
        NotificationDispatchState state,
        DateTimeOffset recoverAfter,
        DateTimeOffset? claimExpiresAt = null
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notificationId,
            ChannelKey = channel,
            State = state,
            RecoverAfter = recoverAfter,
            ClaimExpiresAt = claimExpiresAt,
        };

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));
}
