using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class HttpPollDiffTests
{
    [Fact]
    public void Diff_PollItems_DetectsNewAndChanged()
    {
        var sourceId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var existing = Item(sourceId, "42", "v1", 0, now.AddMinutes(-1));

        var result = HttpPollDiff.Diff(
            sourceId,
            [existing],
            [PollItem("42", "v2", """{"number":42,"state":"open"}"""), PollItem("43", "v1", """{"number":43}""")],
            baseline: false,
            now
        );

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Changes.Count);
        Assert.Collection(
            result.Changes,
            change =>
            {
                Assert.Equal("42", change.ItemKey);
                Assert.Equal(PollItemChangeKind.Changed, change.Kind);
            },
            change =>
            {
                Assert.Equal("43", change.ItemKey);
                Assert.Equal(PollItemChangeKind.New, change.Kind);
            }
        );
        Assert.Equal(1, result.Items.Single(item => item.ItemKey == "42").ChangeSeq);
        Assert.Equal(0, result.Items.Single(item => item.ItemKey == "43").ChangeSeq);
    }

    [Fact]
    public void Diff_ContentReturnsToEarlierValue_IncrementsChangeSeq()
    {
        var sourceId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var item = Item(sourceId, "42", "A", 0, now.AddMinutes(-2));

        var changedToB = HttpPollDiff.Diff(
            sourceId,
            [item],
            [PollItem("42", "B", """{"number":42,"state":"closed"}""")],
            baseline: false,
            now
        );
        Assert.Equal(1, changedToB.Items.Single().ChangeSeq);

        var changedBackToA = HttpPollDiff.Diff(
            sourceId,
            changedToB.Items,
            [PollItem("42", "A", """{"number":42,"state":"open"}""")],
            baseline: false,
            now.AddMinutes(1)
        );

        Assert.Equal(2, changedBackToA.Items.Single().ChangeSeq);
        Assert.Equal(2, changedBackToA.Changes.Single().ChangeSeq);
    }

    private static EventSourceItem Item(
        Guid sourceId,
        string key,
        string fingerprint,
        long changeSeq,
        DateTimeOffset firstSeen
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            SourceId = sourceId,
            ItemKey = key,
            Fingerprint = fingerprint,
            ChangeSeq = changeSeq,
            FirstSeenAt = firstSeen,
            LastSeenAt = firstSeen,
        };

    private static HttpPollItem PollItem(string key, string fingerprint, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new HttpPollItem(key, fingerprint, document.RootElement.Clone());
    }
}
