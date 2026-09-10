using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Projects.Tests;

public partial class EfCoreChatHistoryProviderTests
{
    [Fact]
    public async Task TurnEnd_ManyAppends_ModelSeesImmutablePendingHistoryAndOneCommit()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var initialTime = fixture.Clock.GetUtcNow();
        await using (fixture.BeginScope())
        {
            for (var index = 0; index < 20; index++)
            {
                var message = new ChatMessage(ChatRole.User, $"message-{index}");
                await fixture.AppendAsync([message]);
                message.Contents.Clear();
            }
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(await fixture.ReadRowsAsync());
            Assert.Equal(
                Enumerable.Range(0, 20).Select(index => $"message-{index}"),
                await fixture.ReadModelTextsAsync()
            );
        }

        var records = await fixture.ReadRowsAsync();
        Assert.Equal(20, records.Count);
        Assert.Equal(20, records.Select(record => record.TaskId).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => (long?)index),
            records.Select(record => record.ConversationSequence)
        );
        Assert.All(records, record => Assert.Equal(initialTime, record.CreateTime));
        Assert.Equal(1, fixture.Commits.Count);
    }

    [Fact]
    public async Task Interval_NoFurtherMessages_FlushesAtDeadline()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.Interval);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "pending")]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(await fixture.ReadRowsAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Commits.WaitAsync();

        Assert.Single(await fixture.ReadRowsAsync());
        Assert.Equal(["pending"], await fixture.ReadModelTextsAsync());
        Assert.Equal(1, fixture.Commits.Count);
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, 16777216)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, 1)]
    [InlineData(ConversationHistoryWriteMode.Interval, 1)]
    public async Task Append_ImmediateOrCapacityLimit_FlushesBeforeReturning(
        ConversationHistoryWriteMode mode,
        long capacity
    )
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(mode, capacity);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "capacity")]);

        Assert.Single(await fixture.ReadRowsAsync());
        Assert.Equal(1, fixture.Commits.Count);
    }

    [Fact]
    public async Task Append_MetadataBytes_CrossCapacityAtTheExactCombinedSize()
    {
        // Arrange
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var message = new ChatMessage(ChatRole.User, "中文消息 😀")
        {
            AdditionalProperties = new() { ["mode"] = "plan" },
        };
        var targetId = new string('中', 2048);
        message.Contents[0].AdditionalProperties = new() { ["targetType"] = "agent", ["targetId"] = targetId };
        var metadata = new Dictionary<string, JsonElement>
        {
            ["targetType"] = JsonSerializer.SerializeToElement("agent"),
            ["targetId"] = JsonSerializer.SerializeToElement(targetId),
            ["agentMode"] = JsonSerializer.SerializeToElement("plan"),
        };
        var size =
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(message, options))
            + JsonSerializer.SerializeToUtf8Bytes(metadata).Length;
        await using var fixture = await HistoryBatchFixture.CreateAsync(
            ConversationHistoryWriteMode.TurnEnd,
            capacity: size * 3L,
            jsonSerializerOptions: options
        );
        await using var scope = fixture.BeginScope();

        // Act
        await fixture.AppendAsync([message, message]);
        Assert.Empty(await fixture.ReadRowsAsync());
        await fixture.AppendAsync([message]);

        // Assert
        var records = await fixture.ReadRowsAsync();
        Assert.Equal(3, records.Count);
        Assert.All(records, record => Assert.Equal("plan", record.Metadata!["agentMode"].GetString()));
        Assert.Equal(1, fixture.Commits.Count);
    }

    [Fact]
    public async Task NestedScope_Disposal_DoesNotFlushOuterTurn()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var outer = fixture.BeginScope();
        await using (fixture.BeginScope())
            await fixture.AppendAsync([new(ChatRole.User, "node")]);

        Assert.Empty(await fixture.ReadRowsAsync());
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(await fixture.ReadRowsAsync());
    }

    [Fact]
    public async Task Flush_CommitAcknowledgementFails_RetryDoesNotDuplicateRecords()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "once")]);
        fixture.Commits.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken)
        );
        var firstId = Assert.Single(await fixture.ReadRowsAsync()).Id;
        Assert.Equal(["once"], await fixture.ReadModelTextsAsync());
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(firstId, Assert.Single(await fixture.ReadRowsAsync()).Id);
        Assert.Equal(1, fixture.Commits.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flush_ResetOrDeletion_DoesNotResurrectHistory(bool delete)
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "stale")]);
        await using (var db = fixture.CreateContext())
        {
            if (delete)
                await db.ProjectConversations.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            else
                await db.ProjectConversations.ExecuteUpdateAsync(
                    update => update.SetProperty(item => item.Generation, 1),
                    TestContext.Current.CancellationToken
                );
        }
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken)
        );
        ConversationHistoryPersistenceContext.RecordFailure(error);

        Assert.Empty(await fixture.ReadRowsAsync());
        await using var check = fixture.CreateContext();
        Assert.Equal(
            delete ? 0 : 1,
            await check.ProjectConversations.CountAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Scope_OwnershipLost_DropsPendingHistory()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        using var lost = new CancellationTokenSource();
        var scope = fixture.BeginScope(lost.Token);
        await fixture.AppendAsync([new(ChatRole.User, "stale")]);
        lost.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.DisposeAsync().AsTask());

        Assert.Empty(await fixture.ReadRowsAsync());
    }

    [Fact]
    public async Task Scope_ExecutionFails_PreservesFailureAndFlushesHistory()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var failure = new OperationCanceledException("execution canceled");
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await using var scope = fixture.BeginScope();
            await fixture.AppendAsync([new(ChatRole.User, "before cancel")]);
            fixture.Commits.ThrowAfterCommit = true;
            await ConversationHistoryPersistenceContext.ObserveAsync(Task.FromException<int>(failure));
        });

        Assert.Same(failure, observed);
        Assert.Single(await fixture.ReadRowsAsync());
    }

    [Fact]
    public async Task Scope_ParallelAppendsAndFlushes_PreservesEveryMessageAndUniqueSequence()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        await Task.WhenAll(
            Enumerable
                .Range(0, 40)
                .Select(async index =>
                {
                    await fixture.AppendAsync([new(ChatRole.User, index.ToString())]);
                    if (index % 7 == 0)
                        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);
                    await fixture.ReadModelTextsAsync();
                })
        );
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);

        var rows = await fixture.ReadRowsAsync();
        Assert.Equal(40, rows.Count);
        Assert.Equal(40, rows.Select(row => row.ConversationSequence).Distinct().Count());
        Assert.Equal(40, (await fixture.ReadModelTextsAsync()).Distinct().Count());
    }

    [Fact]
    public async Task Scope_ForeignUser_CannotReadOrAppendPendingMessages()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "private")]);
        using (
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other")], "Test"))
            )
        )
        {
            await Assert.ThrowsAsync<AgwException>(() => fixture.ReadModelTextsAsync());
            await Assert.ThrowsAsync<AgwException>(() => fixture.AppendAsync([new(ChatRole.User, "foreign")]));
        }
        Assert.Equal(["private"], await fixture.ReadModelTextsAsync());
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
    public async Task Barrier_CheckpointBoundary_BlocksConcurrentAppendUntilMarkerIsCommitted()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "before")]);
        Task append;
        await using (
            await ConversationHistoryPersistenceContext.EnterBarrierAsync(TestContext.Current.CancellationToken)
        )
        {
            append = fixture.AppendAsync([new(ChatRole.User, "after")]);
            Assert.False(append.IsCompleted);
            var first = Assert.Single(await fixture.ReadRowsAsync());
            await using var db = fixture.CreateContext();
            db.ProjectConversationChatHistories.Add(
                CreateRecord(
                    first.ConversationId,
                    Guid.NewGuid(),
                    1,
                    "checkpoint",
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                )
            );
            await db.SaveConversationChangesAsync(first.ConversationId, 0, TestContext.Current.CancellationToken);
        }
        await append;
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["before", "checkpoint", "after"], await fixture.ReadModelTextsAsync());
    }

    [Fact]
    public async Task Scope_ResumedGeneration_FlowsToProviderAndRestoresCallerContext()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using (var db = fixture.CreateContext())
            await db.ProjectConversations.ExecuteUpdateAsync(
                update => update.SetProperty(item => item.Generation, 1),
                TestContext.Current.CancellationToken
            );
        await using (
            ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "buffered",
                1,
                TestContext.Current.CancellationToken
            )
        )
        {
            await fixture.AppendAsync([new(ChatRole.User, "new generation")]);
            Assert.Equal(["new generation"], await fixture.ReadModelTextsAsync());
        }

        Assert.Single(await fixture.ReadRowsAsync());
        Assert.False(ConversationSessionContext.IsBound(fixture.ProjectId, "buffered"));
    }

    [Fact]
    public async Task PendingHistory_NodeScopes_RemainIsolatedBeforeAndAfterFlush()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginScope();
        foreach (var node in new[] { "node-a", "node-b" })
        {
            var session = new FakeAgentSession();
            fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId, node);
            await InvokeStoreChatHistoryAsync(
                fixture.Provider,
                new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [new(ChatRole.User, node)], []),
                TestContext.Current.CancellationToken
            );
        }
        Assert.Equal(["node-a"], await fixture.ReadModelTextsAsync("node-a"));
        Assert.Equal(["node-b"], await fixture.ReadModelTextsAsync("node-b"));
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["node-a"], await fixture.ReadModelTextsAsync("node-a"));
    }

    [Fact]
    public async Task Interval_TransientFailure_RetainsBatchAndRetriesNextInterval()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.Interval);
        await using var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "retry")]);
        fixture.FailNextContext = true;
        await fixture
            .Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ContextFailure.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["retry"], await fixture.ReadModelTextsAsync());
        Assert.Empty(await fixture.ReadRowsAsync());
        await fixture
            .Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Commits.WaitAsync();

        Assert.Single(await fixture.ReadRowsAsync());
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

    [Fact]
    public async Task StreamingScope_MultipleMoveNextCalls_RetainsPendingHistoryUntilCompletion()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        async IAsyncEnumerable<int> Execute()
        {
            await using var scope = fixture.BeginScope();
            await fixture.AppendAsync([new(ChatRole.User, "first")]);
            yield return 1;
            await fixture.AppendAsync([new(ChatRole.User, "second")]);
            yield return 2;
        }
        await foreach (
            var value in ConversationHistoryPersistenceContext.RunStreaming(
                Execute(),
                fixture.Provider,
                fixture.ProjectId,
                "buffered",
                0
            )
        )
        {
            Assert.Empty(await fixture.ReadRowsAsync());
        }
        Assert.Equal(2, (await fixture.ReadRowsAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingScope_CancelOrEarlyDisposal_FlushesWhileRestoringTheCaller(bool cancel)
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var failure = new OperationCanceledException("stream canceled");
        async IAsyncEnumerable<int> Execute()
        {
            await fixture.AppendAsync([new(ChatRole.User, "before yield")]);
            yield return 1;
            throw failure;
        }
        var source = ConversationHistoryPersistenceContext.RunStreaming(
            Execute(),
            fixture.Provider,
            fixture.ProjectId,
            "buffered",
            0
        );
        await using (var enumerator = source.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Null(ConversationHistoryPersistenceContext.Current);
            if (cancel)
                Assert.Same(
                    failure,
                    await Assert.ThrowsAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask())
                );
        }
        Assert.Single(await fixture.ReadRowsAsync());
        Assert.Null(ConversationHistoryPersistenceContext.Current);
    }

    [Fact]
    public async Task Scope_DurableHumanWait_DoesNotHideFinalPersistenceFailure()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var scope = fixture.BeginScope();
        await fixture.AppendAsync([new(ChatRole.User, "waiting")]);
        var interruption = new AgwException(ErrorCodes.DurableExecutionConflict);
        ConversationHistoryPersistenceContext.IgnoreInterruption(interruption);
        await Assert.ThrowsAsync<AgwException>(() =>
            ConversationHistoryPersistenceContext.ObserveAsync(Task.FromException<int>(interruption))
        );
        Assert.False(ConversationHistoryPersistenceContext.HasExecutionFailure);
        await using (var db = fixture.CreateContext())
            await db.ProjectConversations.ExecuteUpdateAsync(
                update => update.SetProperty(item => item.Generation, 1),
                TestContext.Current.CancellationToken
            );

        var error = await Assert.ThrowsAsync<AgwException>(() => scope.DisposeAsync().AsTask());
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        Assert.Empty(await fixture.ReadRowsAsync());
    }

    private sealed class HistoryBatchFixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"agw-history-{Guid.NewGuid():N}.db");
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _services;
        public ManualTimeProvider Clock { get; } = new();
        public HistoryCommitCounter Commits { get; } = new();
        public Guid ProjectId { get; } = Guid.CreateVersion7();
        public EfCoreChatHistoryProvider Provider { get; }
        public bool FailNextContext { get; set; }
        public TaskCompletionSource ContextFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HistoryBatchFixture(
            ConversationHistoryWriteMode mode,
            long capacity,
            JsonSerializerOptions? jsonSerializerOptions
        )
        {
            _options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={_path};Foreign Keys=False;Pooling=False")
                .AddInterceptors(Commits)
                .Options;
            _services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ => CreateContext())
                .BuildServiceProvider();
            Provider = new EfCoreChatHistoryProvider(
                _services.GetRequiredService<IServiceScopeFactory>(),
                InMemoryApplicationLock.Shared,
                NullLogger<EfCoreChatHistoryProvider>.Instance,
                Clock,
                jsonSerializerOptions: jsonSerializerOptions,
                options: Options.Create(new ConversationHistoryOptions { Mode = mode, MaxBufferedBytes = capacity })
            );
        }

        public static async Task<HistoryBatchFixture> CreateAsync(
            ConversationHistoryWriteMode mode,
            long capacity = 16777216,
            JsonSerializerOptions? jsonSerializerOptions = null
        )
        {
            var fixture = new HistoryBatchFixture(mode, capacity, jsonSerializerOptions);
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Projects.Add(CreateProject(fixture.ProjectId));
            db.ProjectConversations.Add(
                EfCoreChatHistoryProviderTests.CreateContext(Guid.CreateVersion7(), fixture.ProjectId, "buffered")
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            fixture.Commits.Reset();
            return fixture;
        }

        public IAsyncDisposable BeginScope(CancellationToken? ownershipLost = null) =>
            ConversationHistoryPersistenceContext.BeginScope(
                Provider,
                ProjectId,
                "buffered",
                0,
                ownershipLost ?? TestContext.Current.CancellationToken
            );

        public Task AppendAsync(IReadOnlyList<ChatMessage> messages) =>
            Provider.AppendAsync(ProjectId, "buffered", messages, TestContext.Current.CancellationToken);

        public AgwDbContext CreateContext()
        {
            if (FailNextContext)
            {
                FailNextContext = false;
                ContextFailure.TrySetResult();
                throw new DbUpdateException("Simulated transient database outage.");
            }
            return new(_options);
        }

        public async Task<List<ProjectConversationChatHistory>> ReadRowsAsync()
        {
            await using var db = CreateContext();
            return await db
                .ProjectConversationChatHistories.OrderBy(row => row.ConversationSequence)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public async Task<string[]> ReadModelTextsAsync(string? historyScope = null)
        {
            var session = new FakeAgentSession();
            if (historyScope == null)
                Provider.InitializeSessionState(session, "buffered", ProjectId);
            else
                Provider.InitializeSessionState(session, "buffered", ProjectId, historyScope);
            var messages = await InvokeProvideChatHistoryAsync(
                Provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                TestContext.Current.CancellationToken
            );
            return messages.Select(message => message.Text).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            File.Delete(_path);
        }
    }

    private sealed class HistoryCommitCounter : DbTransactionInterceptor
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

        public Task<int> WaitAsync() =>
            _committed
                .Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
