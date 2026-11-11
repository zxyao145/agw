using System.Text.Json;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application.Persistence;

public sealed record HttpPollItem(string Key, string Fingerprint, JsonElement Data);

public enum PollItemChangeKind
{
    New,
    Changed,
}

public sealed record HttpPollItemChange
{
    public required string ItemKey { get; init; }

    public required PollItemChangeKind Kind { get; init; }

    public required long ChangeSeq { get; init; }

    public required JsonElement Data { get; init; }
}

public sealed record HttpPollDiffResult
{
    public required IReadOnlyList<EventSourceItem> Items { get; init; }

    public required IReadOnlyList<HttpPollItemChange> Changes { get; init; }
}

public static class HttpPollDiff
{
    public static HttpPollDiffResult Diff(
        Guid sourceId,
        IReadOnlyCollection<EventSourceItem> savedItems,
        IReadOnlyList<HttpPollItem> observedItems,
        bool baseline,
        DateTimeOffset now
    )
    {
        var itemsByKey = savedItems.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var items = savedItems.ToList();
        var changes = new List<HttpPollItemChange>();

        foreach (var observed in observedItems)
        {
            if (!observedKeys.Add(observed.Key))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "The HTTP poll returned a duplicate item key.");
            }

            if (!itemsByKey.TryGetValue(observed.Key, out var saved))
            {
                saved = new EventSourceItem
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = sourceId,
                    ItemKey = observed.Key,
                    Fingerprint = observed.Fingerprint,
                    ChangeSeq = 0,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                };
                itemsByKey.Add(observed.Key, saved);
                items.Add(saved);

                if (!baseline)
                {
                    changes.Add(CreateChange(observed, PollItemChangeKind.New, saved.ChangeSeq));
                }

                continue;
            }

            saved.LastSeenAt = now;
            if (baseline || saved.Fingerprint == observed.Fingerprint)
            {
                saved.Fingerprint = observed.Fingerprint;
                continue;
            }

            saved.Fingerprint = observed.Fingerprint;
            saved.ChangeSeq++;
            changes.Add(CreateChange(observed, PollItemChangeKind.Changed, saved.ChangeSeq));
        }

        return new HttpPollDiffResult { Items = items, Changes = changes };
    }

    private static HttpPollItemChange CreateChange(HttpPollItem item, PollItemChangeKind kind, long changeSeq) =>
        new()
        {
            ItemKey = item.Key,
            Kind = kind,
            ChangeSeq = changeSeq,
            Data = item.Data.Clone(),
        };
}
