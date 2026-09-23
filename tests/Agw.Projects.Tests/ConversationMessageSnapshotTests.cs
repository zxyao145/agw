using System.Text.Json;
using Agw.Projects.Application.History;
using Agw.Projects.Contracts.History;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public partial class EfCoreChatHistoryProviderTests
{
    [Fact]
    public async Task UpsertSnapshots_OrderedUpdatesAndRetry_PreserveOneRow()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;

        await fixture.Provider.UpsertAsync(scope, [Snapshot(id, "a")], token);
        var initial = Assert.Single(await fixture.ReadRowsAsync());
        var complete = Snapshot(id, "ab");
        await fixture.Provider.UpsertAsync(scope, [complete], token);
        await fixture.Provider.UpsertAsync(scope, [complete], token);

        var row = Assert.Single(await fixture.ReadRowsAsync());
        Assert.Equal("ab", row.GetText());
        Assert.Equal(initial.Id, row.Id);
        Assert.Equal(initial.TaskId, row.TaskId);
        Assert.Equal(initial.CreateTime, row.CreateTime);
        Assert.Equal(initial.ConversationSequence, row.ConversationSequence);
    }

    [Fact]
    public async Task UpsertSnapshots_ForeignOwnerOrGeneration_RejectsMutation()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await fixture.Provider.UpsertAsync(scope, [Snapshot(id, "original")], token);

        var generationError = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Provider.UpsertAsync(scope with { Generation = 1 }, [Snapshot(id, "wrong")], token)
        );
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, generationError.Code);
        using (
            UserInfoUtil.Push(
                new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        [
                            new System.Security.Claims.Claim(
                                System.Security.Claims.ClaimTypes.NameIdentifier,
                                "foreign-owner"
                            ),
                        ],
                        "test"
                    )
                )
            )
        )
        {
            // 非当前用户的项目写入直接停止，不产生新行也不修改原行。
            // A write aimed at another user's project stops before any row is added or changed.
            await fixture.Provider.UpsertAsync(scope, [Snapshot(id, "wrong")], token);
        }

        Assert.Equal("original", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task UpsertSnapshots_ForeignProducer_KeepsExistingRowAndStoresOtherMessages()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var owned = Guid.NewGuid();
        var other = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await fixture.Provider.UpsertAsync(scope, [Snapshot(owned, "original")], token);

        await fixture.Provider.UpsertAsync(
            scope with
            {
                ProducerId = Guid.NewGuid(),
            },
            [Snapshot(owned, "wrong"), Snapshot(other, "second")],
            token
        );

        var rows = await fixture.ReadRowsAsync();
        Assert.Equal("original", Assert.Single(rows, row => row.Id == owned).GetText());
        Assert.Equal("second", Assert.Single(rows, row => row.Id == other).GetText());
    }

    [Fact]
    public async Task Schedule_ConcurrentCalls_CaptureAfterEarlierWriteCompletes()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var id = Guid.NewGuid();
        var source = new SnapshotSource { Current = Snapshot(id, "a") };
        var token = TestContext.Current.CancellationToken;
        Task first;
        Task second;
        await using (
            await InMemoryApplicationLock.Shared.AcquireAsync(
                $"conversation-history:{fixture.ProjectId:D}:buffered",
                token
            )
        )
        {
            first = fixture.Provider.ScheduleAsync(scope, source, 1, token);
            Assert.Equal(1, source.CaptureCount);
            source.Current = Snapshot(id, "ab");
            second = fixture.Provider.ScheduleAsync(scope, source, 1, token);
            Assert.Equal(1, source.CaptureCount);
        }

        await Task.WhenAll(first, second);

        Assert.Equal(2, source.CaptureCount);
        Assert.Equal("ab", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task Schedule_SourceAlreadyWritten_SkipsSecondRoundTrip()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var source = new SnapshotSource { Current = Snapshot(Guid.NewGuid(), "a") };
        var token = TestContext.Current.CancellationToken;

        await fixture.Provider.ScheduleAsync(scope, source, 1, token);
        var commits = fixture.Commits.Count;
        await fixture.Provider.ScheduleAsync(scope, source, 1, token);

        Assert.Equal(commits, fixture.Commits.Count);
        Assert.Equal(2, source.CaptureCount);
        Assert.Single(source.Acknowledged);
        Assert.Equal("a", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task Schedule_LostCommitAcknowledgement_RetriesWithoutDuplicateRows()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var scope = fixture.Provider.GetMessageWriteScope(session);
        var source = new SnapshotSource { Current = Snapshot(Guid.NewGuid(), "a") };
        var token = TestContext.Current.CancellationToken;
        fixture.Commits.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Provider.ScheduleAsync(scope, source, 1, token));
        Assert.Empty(source.Acknowledged);
        await fixture.Provider.ScheduleAsync(scope, source, 1, token);

        Assert.Single(source.Acknowledged);
        Assert.Equal("a", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    private sealed class SnapshotSource : IConversationMessageSource
    {
        private ConversationMessageSnapshot? _clean;

        public required ConversationMessageSnapshot Current { get; set; }
        public int CaptureCount { get; private set; }
        public List<ConversationMessageSnapshot> Acknowledged { get; } = [];

        public IReadOnlyList<Guid> GetPendingMessageIds() =>
            ReferenceEquals(_clean, Current) ? [] : [Current.MessageId];

        public IReadOnlyList<ConversationMessageSnapshot> CapturePending()
        {
            CaptureCount++;
            return ReferenceEquals(_clean, Current) ? [] : [Current];
        }

        public void Acknowledge(IReadOnlyList<ConversationMessageSnapshot> snapshots)
        {
            Acknowledged.AddRange(snapshots);
            if (snapshots.Any(snapshot => ReferenceEquals(snapshot, Current)))
                _clean = Current;
        }
    }

    private static ConversationMessageSnapshot Snapshot(Guid id, string text) =>
        new()
        {
            MessageId = id,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            Metadata = [],
            Payload = JsonSerializer.Serialize(
                new ChatMessage(ChatRole.Assistant, text) { MessageId = id.ToString("D") },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ),
        };
}
