using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Projects.Tests;

/// <summary>
/// ConversationHistoryStore 的按消息身份写入、所属与 Generation 校验、批量缓冲的捕获与确认，以及写入选项。
/// ConversationHistoryStore writes by message identity, ownership and generation checks, capture and acknowledgement of the batching buffer, and write options.
/// </summary>
public sealed class ConversationHistoryStoreTests : IDisposable
{
    private const string ContextId = "buffered";

    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task UpsertAsync_OrderedUpdatesAndRetry_PreserveOneRow()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var scope = fixture.WriteScope();
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;

        await fixture.Store.UpsertAsync(scope, [Snapshot(id, "a")], token);
        var initial = Assert.Single(await fixture.ReadRowsAsync());
        var complete = Snapshot(id, "ab");
        await fixture.Store.UpsertAsync(scope, [complete], token);
        await fixture.Store.UpsertAsync(scope, [complete], token);

        var row = Assert.Single(await fixture.ReadRowsAsync());
        Assert.Equal("ab", row.GetText());
        Assert.Equal(initial.Id, row.Id);
        Assert.Equal(initial.TaskId, row.TaskId);
        Assert.Equal(initial.CreateTime, row.CreateTime);
        Assert.Equal(initial.ConversationSequence, row.ConversationSequence);
    }

    [Fact]
    public async Task UpsertAsync_ForeignOwnerOrGeneration_RejectsMutation()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var scope = fixture.WriteScope();
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await fixture.Store.UpsertAsync(scope, [Snapshot(id, "original")], token);

        var generationError = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Store.UpsertAsync(scope with { Generation = 1 }, [Snapshot(id, "wrong")], token)
        );
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, generationError.Code);
        using (
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "foreign-owner")], "test"))
            )
        )
        {
            // 非当前用户的项目写入直接停止，不产生新行也不修改原行。
            // A write aimed at another user's project stops before any row is added or changed.
            await fixture.Store.UpsertAsync(scope, [Snapshot(id, "wrong")], token);
        }

        Assert.Equal("original", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task UpsertAsync_ForeignProducer_KeepsExistingRowAndStoresOtherMessages()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var scope = fixture.WriteScope();
        var owned = Guid.NewGuid();
        var other = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await fixture.Store.UpsertAsync(scope, [Snapshot(owned, "original")], token);

        await fixture.Store.UpsertAsync(
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
    public async Task Buffer_ConcurrentSchedules_CaptureAfterEarlierWriteCompletes()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var buffer = fixture.BeginBuffer();
        var scope = fixture.WriteScope();
        var id = Guid.NewGuid();
        var source = new SnapshotSource { Current = Snapshot(id, "a") };
        var token = TestContext.Current.CancellationToken;
        Task first;
        Task second;
        await using (
            await InMemoryApplicationLock.Shared.AcquireAsync(
                $"conversation-history:{fixture.ProjectId:D}:{ContextId}",
                token
            )
        )
        {
            first = buffer.ScheduleAsync(scope, source, 1, token);
            Assert.Equal(1, source.CaptureCount);
            source.Current = Snapshot(id, "ab");
            second = buffer.ScheduleAsync(scope, source, 1, token);
            Assert.Equal(1, source.CaptureCount);
        }

        await Task.WhenAll(first, second);
        await buffer.CompleteAsync(null);

        Assert.Equal(2, source.CaptureCount);
        Assert.Equal("ab", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task Buffer_SourceAlreadyWritten_SkipsSecondRoundTrip()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var buffer = fixture.BeginBuffer();
        var scope = fixture.WriteScope();
        var source = new SnapshotSource { Current = Snapshot(Guid.NewGuid(), "a") };
        var token = TestContext.Current.CancellationToken;

        await buffer.ScheduleAsync(scope, source, 1, token);
        var commits = fixture.Commits.Count;
        await buffer.ScheduleAsync(scope, source, 1, token);
        await buffer.CompleteAsync(null);

        Assert.Equal(commits, fixture.Commits.Count);
        Assert.Single(source.Acknowledged);
        Assert.Equal("a", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    [Fact]
    public async Task Buffer_LostCommitAcknowledgement_RetriesWithoutDuplicateRows()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.Immediate);
        var buffer = fixture.BeginBuffer();
        var scope = fixture.WriteScope();
        var source = new SnapshotSource { Current = Snapshot(Guid.NewGuid(), "a") };
        var token = TestContext.Current.CancellationToken;
        fixture.Commits.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => buffer.ScheduleAsync(scope, source, 1, token));
        Assert.Empty(source.Acknowledged);
        await buffer.ScheduleAsync(scope, source, 1, token);
        await buffer.CompleteAsync(null);

        Assert.Single(source.Acknowledged);
        var row = Assert.Single(await fixture.ReadRowsAsync());
        Assert.Equal("a", row.GetText());
        Assert.Equal(row.ConversationSequence, buffer.CommittedSequence);
    }

    [Fact]
    public async Task ReadAsync_PendingBuffer_MergesUncommittedSnapshotsInOrder()
    {
        await using var fixture = await StoreFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var token = TestContext.Current.CancellationToken;
        var scope = fixture.WriteScope();
        await fixture.Store.UpsertAsync(scope, [Snapshot(Guid.NewGuid(), "committed")], token);
        var buffer = fixture.BeginBuffer();
        await buffer.ScheduleAsync(
            scope,
            new SnapshotSource { Current = Snapshot(Guid.NewGuid(), "pending") },
            1,
            token
        );

        var entries = await fixture.Store.ReadAsync(fixture.Conversation(), null, buffer, token);
        var committedOnly = await fixture.Store.ReadAsync(fixture.Conversation(), null, null, token);

        Assert.Equal(["committed", "pending"], entries.Select(Text));
        Assert.Equal(["committed"], committedOnly.Select(Text));
        Assert.Single(await fixture.ReadRowsAsync());
        await buffer.CompleteAsync(null);
        Assert.Equal(2, (await fixture.ReadRowsAsync()).Count);
        Assert.Equal(1, buffer.CommittedSequence);
    }

    [Theory]
    [InlineData("Unknown", "5", "10")]
    [InlineData("Interval", "0", "10")]
    [InlineData("TurnEnd", "5", "0")]
    public void HistoryOptions_InvalidConfiguration_IsRejected(string mode, string interval, string capacity)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConversationHistory:Mode"] = mode,
                    ["ConversationHistory:FlushIntervalSeconds"] = interval,
                    ["ConversationHistory:MaxBufferedBytes"] = capacity,
                }
            )
            .Build();
        using var services = new ServiceCollection().AddProjects(configuration).BuildServiceProvider();
        Assert.ThrowsAny<Exception>(() => services.GetRequiredService<IOptions<ConversationHistoryOptions>>().Value);
    }

    [Fact]
    public void HistoryOptions_DefaultsAndOverrides_AreBoundWithoutChangingDeploymentDefaults()
    {
        var empty = new ConfigurationBuilder().Build();
        using var defaults = new ServiceCollection().AddProjects(empty).BuildServiceProvider();
        var options = defaults.GetRequiredService<IOptions<ConversationHistoryOptions>>().Value;
        Assert.Equal(ConversationHistoryWriteMode.Interval, options.Mode);
        Assert.Equal(5, options.FlushIntervalSeconds);
        Assert.Equal(16777216, options.MaxBufferedBytes);
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConversationHistory:Mode"] = "TurnEnd",
                    ["ConversationHistory:FlushIntervalSeconds"] = "20",
                    ["ConversationHistory:MaxBufferedBytes"] = "8192",
                }
            )
            .Build();
        using var overrides = new ServiceCollection().AddProjects(configured).BuildServiceProvider();
        var actual = overrides.GetRequiredService<IOptions<ConversationHistoryOptions>>().Value;
        Assert.Equal(ConversationHistoryWriteMode.TurnEnd, actual.Mode);
        Assert.Equal(20, actual.FlushIntervalSeconds);
        Assert.Equal(8192, actual.MaxBufferedBytes);
    }

    private static string? Text(ConversationHistoryEntry entry) =>
        JsonSerializer
            .Deserialize<ChatMessage>(entry.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?.Text;

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

    /// <summary>
    /// 一条消息的投影来源：确认后不再有待写内容，内容变化后重新变为待写。
    /// The projection source of one message: nothing is pending after acknowledgement, and a content change makes it pending again.
    /// </summary>
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

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            AppContext.BaseDirectory,
            "test-databases",
            $"agw-history-{Guid.NewGuid():N}.db"
        );
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _services;

        private StoreFixture(ConversationHistoryWriteMode mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={_path};Foreign Keys=False;Pooling=False")
                .AddInterceptors(Commits)
                .Options;
            _services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ => new AgwDbContext(_options))
                .BuildServiceProvider();
            Store = new ConversationHistoryStore(
                _services.GetRequiredService<IServiceScopeFactory>(),
                InMemoryApplicationLock.Shared,
                NullLogger<ConversationHistoryStore>.Instance,
                Clock,
                options: Options.Create(new ConversationHistoryOptions { Mode = mode })
            );
        }

        public ManualTimeProvider Clock { get; } = new();
        public CommitCounter Commits { get; } = new();
        public Guid ProjectId { get; } = Guid.CreateVersion7();
        public ConversationHistoryStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(ConversationHistoryWriteMode mode)
        {
            var fixture = new StoreFixture(mode);
            await using var db = new AgwDbContext(fixture._options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Projects.Add(
                new Project
                {
                    Id = fixture.ProjectId,
                    Name = "Chat Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                }
            );
            db.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = fixture.ProjectId,
                    ContextId = ContextId,
                    Title = "Chat",
                    CreateBy = "tester",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            fixture.Commits.Reset();
            return fixture;
        }

        public ConversationHistoryScope Conversation() =>
            new()
            {
                ProjectId = ProjectId,
                ContextId = ContextId,
                Generation = 0,
                IsExecutionBound = false,
            };

        public ConversationMessageWriteScope WriteScope() =>
            new()
            {
                ProjectId = ProjectId,
                ContextId = ContextId,
                Generation = 0,
                ProducerId = Guid.NewGuid(),
            };

        public IConversationHistoryBuffer BeginBuffer() => Store.BeginBuffer(Conversation());

        public async Task<List<ProjectConversationChatHistory>> ReadRowsAsync()
        {
            await using var db = new AgwDbContext(_options);
            return await db
                .ProjectConversationChatHistories.OrderBy(row => row.ConversationSequence)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            File.Delete(_path);
        }
    }

    private sealed class CommitCounter : DbTransactionInterceptor
    {
        private readonly Channel<int> _committed = Channel.CreateUnbounded<int>();
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool ThrowAfterCommit { get; set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            _committed.Writer.TryWrite(Interlocked.Increment(ref _count));
            if (ThrowAfterCommit)
            {
                ThrowAfterCommit = false;
                throw new DbUpdateException("Simulated lost commit acknowledgement.");
            }
            return Task.CompletedTask;
        }

        public void Reset()
        {
            _count = 0;
            while (_committed.Reader.TryRead(out _)) { }
        }
    }
}
