using Agw.Notifications.Contracts;
using Agw.Notifications.Infrastructure.Stream;
using Xunit;

namespace Agw.Notifications.Tests;

public sealed class NotificationStreamHubTests
{
    [Fact]
    public async Task Subscribe_ReconnectOverlap_PublishesLateCommitOnce()
    {
        var hub = new NotificationStreamHub();
        var now = new DateTimeOffset(2026, 11, 8, 1, 0, 10, TimeSpan.Zero);
        var lastChangedAt = now.AddSeconds(-1);
        var notificationId = Guid.CreateVersion7();
        await using var subscription = hub.Subscribe("owner-1", $"{lastChangedAt:O}|{notificationId:D}", now);
        var lateCommit = new NotificationStreamItem
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now.AddSeconds(-4),
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Late commit",
            Text = "This row committed after the previous scan.",
            Link = null,
            ReadAt = null,
            ChangedAt = lastChangedAt.AddSeconds(-3),
            Version = 1,
        };

        Assert.True(hub.GetPollBatches().Single().From <= lateCommit.ChangedAt);
        hub.Publish("owner-1", lateCommit);
        Assert.True(subscription.Reader.TryRead(out var delivered));
        hub.Publish("owner-1", lateCommit);

        Assert.Equal(lateCommit, delivered!.Notification);
        Assert.Equal($"{lastChangedAt:O}|{notificationId:D}", delivered.EventId);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Publish_ReadChangeWithSameVersionAndTimestamp_EmitsUpdatedSnapshotOnce()
    {
        var hub = new NotificationStreamHub();
        var now = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
        await using var subscription = hub.Subscribe("owner-1", null, now);
        var changedAt = now.AddSeconds(1);
        var notification = new NotificationStreamItem
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = changedAt,
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Read state",
            Text = "The notification is read.",
            Link = null,
            ReadAt = null,
            ChangedAt = changedAt,
            Version = 1,
        };
        hub.Publish("owner-1", notification);
        Assert.True(subscription.Reader.TryRead(out var original));

        var read = notification with { ReadAt = changedAt };
        hub.Publish("owner-1", read);
        Assert.True(subscription.Reader.TryRead(out var updated));
        hub.Publish("owner-1", read);

        Assert.Equal(notification, original!.Notification);
        Assert.Equal(read, updated!.Notification);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Publish_SameVersionAndTimestamp_DoesNotRevertReadState()
    {
        var hub = new NotificationStreamHub();
        var now = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
        await using var subscription = hub.Subscribe("owner-1", null, now);
        var changedAt = now.AddSeconds(1);
        var notification = new NotificationStreamItem
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = changedAt,
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Read state",
            Text = "The notification is unread.",
            Link = null,
            ReadAt = null,
            ChangedAt = changedAt,
            Version = 1,
        };
        hub.Publish("owner-1", notification);
        Assert.True(subscription.Reader.TryRead(out _));

        var read = notification with { ReadAt = changedAt };
        hub.Publish("owner-1", read);
        Assert.True(subscription.Reader.TryRead(out _));
        hub.Publish("owner-1", notification);

        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Publish_LateItemAfterNewerItem_KeepsEventCursorMonotonic()
    {
        var hub = new NotificationStreamHub();
        var now = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
        await using var subscription = hub.Subscribe("owner-1", null, now);
        var notificationId = Guid.CreateVersion7();
        var newer = new NotificationStreamItem
        {
            Id = notificationId,
            CreatedAt = now.AddSeconds(2),
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Newer row",
            Text = "This row was visible first.",
            Link = null,
            ReadAt = null,
            ChangedAt = now.AddSeconds(2),
            Version = 1,
        };
        var late = new NotificationStreamItem
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now.AddSeconds(1),
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Late row",
            Text = "This row committed after the newer row.",
            Link = null,
            ReadAt = null,
            ChangedAt = now.AddSeconds(1),
            Version = 1,
        };

        hub.Publish("owner-1", newer);
        Assert.True(subscription.Reader.TryRead(out var newerDelivery));
        hub.Publish("owner-1", late);
        Assert.True(subscription.Reader.TryRead(out var lateDelivery));

        Assert.True(string.CompareOrdinal(lateDelivery!.EventId, newerDelivery!.EventId) >= 0);
        Assert.Equal(late, lateDelivery.Notification);
    }

    [Fact]
    public async Task Subscribe_OlderReconnectCursor_DoesNotReplayOldRowsToExistingConnection()
    {
        var hub = new NotificationStreamHub();
        var now = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
        await using var existing = hub.Subscribe("owner-1", null, now);
        var item = new NotificationStreamItem
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now.AddSeconds(1),
            Kind = NotificationKind.Notice,
            State = NotificationState.Open,
            Title = "Existing row",
            Text = "This row was already sent.",
            Link = null,
            ReadAt = null,
            ChangedAt = now.AddSeconds(1),
            Version = 1,
        };
        hub.Publish("owner-1", item);
        Assert.True(existing.Reader.TryRead(out _));
        hub.AdvanceWatermark("owner-1", now.AddSeconds(20));
        var oldEventAt = now.AddMinutes(-1);
        await using var reconnect = hub.Subscribe(
            "owner-1",
            $"{oldEventAt:O}|{Guid.CreateVersion7():D}",
            now.AddSeconds(21)
        );

        Assert.True(hub.GetPollBatches().Single().From < now.AddSeconds(15));
        hub.Publish("owner-1", item);

        Assert.False(existing.Reader.TryRead(out _));
        Assert.True(reconnect.Reader.TryRead(out var replay));
        Assert.Equal(item, replay!.Notification);
    }
}
