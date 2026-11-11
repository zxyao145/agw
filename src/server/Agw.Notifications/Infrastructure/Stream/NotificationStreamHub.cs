using System.Globalization;
using System.Threading.Channels;
using Agw.Notifications.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Notifications.Infrastructure.Stream;

public sealed class NotificationStreamHub
{
    internal static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, NotificationStreamSubscription> _subscriptions = [];
    private readonly Dictionary<string, DateTimeOffset> _scanWatermarks = new(StringComparer.Ordinal);

    internal readonly record struct StreamCursor(DateTimeOffset ChangedAt, Guid Id);

    public NotificationStreamSubscription Subscribe(string userId, string? lastEventId, DateTimeOffset now)
    {
        var cursor = ParseCursor(lastEventId) ?? new StreamCursor(now, Guid.Empty);
        var replayFrom =
            cursor.ChangedAt <= DateTimeOffset.MinValue.Add(CursorOverlap)
                ? DateTimeOffset.MinValue
                : cursor.ChangedAt.Subtract(CursorOverlap);
        var subscription = new NotificationStreamSubscription(
            Guid.CreateVersion7(),
            userId,
            cursor,
            new StreamCursor(replayFrom, Guid.Empty),
            this
        );
        lock (_sync)
        {
            _subscriptions.Add(subscription.Id, subscription);
            if (!_scanWatermarks.TryGetValue(userId, out var watermark) || cursor.ChangedAt < watermark)
                _scanWatermarks[userId] = cursor.ChangedAt;
        }
        return subscription;
    }

    public IReadOnlyList<NotificationStreamOwnerBatch> GetPollBatches()
    {
        lock (_sync)
        {
            return _subscriptions
                .Values.GroupBy(subscription => subscription.UserId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var watermark = _scanWatermarks[group.Key];
                    var from =
                        watermark <= DateTimeOffset.MinValue.Add(CursorOverlap)
                            ? DateTimeOffset.MinValue
                            : watermark.Subtract(CursorOverlap);
                    return new NotificationStreamOwnerBatch(group.Key, from, group.ToArray());
                })
                .ToArray();
        }
    }

    public void Publish(string userId, NotificationStreamItem item)
    {
        lock (_sync)
        {
            foreach (var subscription in _subscriptions.Values.Where(subscription => subscription.UserId == userId))
            {
                var afterCursor = Compare(item.ChangedAt, item.Id, subscription.Cursor) > 0;
                if (!afterCursor && item.ChangedAt < subscription.ReplayFrom.ChangedAt)
                    continue;
                if (!subscription.CanPublish(item))
                    continue;
                var eventCursor = afterCursor ? new StreamCursor(item.ChangedAt, item.Id) : subscription.Cursor;
                var delivery = new NotificationStreamDelivery(item, $"{eventCursor.ChangedAt:O}|{eventCursor.Id:D}");
                if (!subscription.Writer.TryWrite(delivery))
                    continue;
                subscription.Remember(item);
                if (afterCursor)
                    subscription.Cursor = eventCursor;
            }
        }
    }

    public void AdvanceWatermark(string userId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_scanWatermarks.ContainsKey(userId))
                _scanWatermarks[userId] = now;
            var cutoff =
                now <= DateTimeOffset.MinValue.Add(CursorOverlap)
                    ? DateTimeOffset.MinValue
                    : now.Subtract(CursorOverlap);
            foreach (var subscription in _subscriptions.Values.Where(subscription => subscription.UserId == userId))
            {
                subscription.AdvanceReplayFloor(cutoff);
                subscription.PruneBefore(cutoff);
            }
        }
    }

    internal void Unsubscribe(Guid subscriptionId)
    {
        lock (_sync)
        {
            if (!_subscriptions.Remove(subscriptionId, out var subscription))
                return;
            if (!_subscriptions.Values.Any(value => value.UserId == subscription.UserId))
                _scanWatermarks.Remove(subscription.UserId);
        }
    }

    private static StreamCursor? ParseCursor(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return null;
        var separator = eventId.IndexOf('|');
        if (
            separator < 1
            || separator == eventId.Length - 1
            || !DateTimeOffset.TryParse(
                eventId.AsSpan(0, separator),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var changedAt
            )
            || !Guid.TryParse(eventId.AsSpan(separator + 1), out var id)
        )
            throw new AgwException(ErrorCodes.InvalidParam, "Last-Event-ID is invalid.");
        return new StreamCursor(changedAt, id);
    }

    private static int Compare(DateTimeOffset changedAt, Guid id, StreamCursor cursor)
    {
        var timeComparison = changedAt.CompareTo(cursor.ChangedAt);
        return timeComparison != 0 ? timeComparison : string.CompareOrdinal(id.ToString("D"), cursor.Id.ToString("D"));
    }
}

public sealed record NotificationStreamOwnerBatch(
    string UserId,
    DateTimeOffset From,
    IReadOnlyList<NotificationStreamSubscription> Subscriptions
);

public sealed record NotificationStreamDelivery(NotificationStreamItem Notification, string EventId);

public sealed class NotificationStreamSubscription : IAsyncDisposable
{
    private readonly NotificationStreamHub _hub;
    private readonly Dictionary<Guid, StreamRevision> _published = [];
    private NotificationStreamHub.StreamCursor _replayFrom;
    private readonly Channel<NotificationStreamDelivery> _channel = Channel.CreateUnbounded<NotificationStreamDelivery>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    internal NotificationStreamSubscription(
        Guid id,
        string userId,
        NotificationStreamHub.StreamCursor cursor,
        NotificationStreamHub.StreamCursor replayFrom,
        NotificationStreamHub hub
    )
    {
        Id = id;
        UserId = userId;
        Cursor = cursor;
        _replayFrom = replayFrom;
        _hub = hub;
    }

    public Guid Id { get; }

    public string UserId { get; }

    public ChannelReader<NotificationStreamDelivery> Reader => _channel.Reader;

    internal ChannelWriter<NotificationStreamDelivery> Writer => _channel.Writer;

    internal NotificationStreamHub.StreamCursor Cursor { get; set; }

    internal NotificationStreamHub.StreamCursor ReplayFrom => _replayFrom;

    internal void AdvanceReplayFloor(DateTimeOffset cutoff)
    {
        if (cutoff > _replayFrom.ChangedAt)
            _replayFrom = new NotificationStreamHub.StreamCursor(cutoff, Guid.Empty);
    }

    internal bool CanPublish(NotificationStreamItem item)
    {
        if (!_published.TryGetValue(item.Id, out var revision))
            return true;
        return item.Version > revision.Version
            || (
                item.Version == revision.Version
                && (
                    item.ChangedAt > revision.ChangedAt
                    || (item.ChangedAt == revision.ChangedAt && revision.ReadAt is null && item.ReadAt is not null)
                )
            );
    }

    internal void Remember(NotificationStreamItem item) =>
        _published[item.Id] = new(item.Version, item.ChangedAt, item.ReadAt);

    internal void PruneBefore(DateTimeOffset cutoff)
    {
        foreach (
            var id in _published.Where(entry => entry.Value.ChangedAt < cutoff).Select(entry => entry.Key).ToArray()
        )
            _published.Remove(id);
    }

    public ValueTask DisposeAsync()
    {
        _hub.Unsubscribe(Id);
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private readonly record struct StreamRevision(long Version, DateTimeOffset ChangedAt, DateTimeOffset? ReadAt);
}
