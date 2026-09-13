using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Channels;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingHistory_LongModelCall_PersistsInputAndProgressBeforeCompletionWithoutDuplicates(
        bool messageIds
    )
    {
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        await using var capabilities = CreateCapabilities();
        var model = new PausedChatClient { IncludeMessageIds = messageIds };
        var history = fixture.Provider;
        var inner = model.AsAgwAgent(
            CreateDefinition(history),
            capabilities,
            NullLoggerFactory.Instance,
            fixture.Services
        );
        var agent = CreateAgent(inner, history, "private memory");
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);
        await using var enumerator = ConversationHistoryPersistenceContext
            .RunStreaming(
                agent.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                ),
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var first = enumerator.MoveNextAsync().AsTask();
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(model.Input, message => message.Role == ChatRole.User);
        await fixture.AdvanceFlushTimerAsync();
        await fixture.WaitForCommitAsync();
        Assert.Equal("question", Assert.Single(await fixture.ReadAsync()).GetText());
        Assert.False(first.IsCompleted);

        model.Emit("first ");
        Assert.True(await first);
        var second = enumerator.MoveNextAsync().AsTask();
        await fixture.AdvanceFlushTimerAsync();
        await fixture.WaitForCommitAsync();
        var partial = await fixture.ReadAsync();
        Assert.Equal(["question", "first "], partial.Select(row => row.GetText()));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(partial[1].ToChatMessage()!));
        Assert.Equal(partial[0].TaskId, partial[1].TaskId);

        model.Emit("second");
        Assert.True(await second);
        var end = enumerator.MoveNextAsync().AsTask();
        await fixture.AdvanceFlushTimerAsync();
        await fixture.WaitForCommitAsync();
        var later = await fixture.ReadAsync();
        Assert.Equal(partial[1].Id, later[1].Id);
        Assert.Equal("first second", later[1].GetText());
        model.Finish();
        while (await end)
            end = enumerator.MoveNextAsync().AsTask();
        await enumerator.DisposeAsync();

        var completed = await fixture.ReadAsync();
        Assert.Equal(2, completed.Count);
        Assert.Equal(partial[1].Id, completed[1].Id);
        Assert.Equal(partial[1].ConversationSequence, completed[1].ConversationSequence);
        Assert.Equal(partial[1].CreateTime, completed[1].CreateTime);
        Assert.True(completed[1].UpdateTime > partial[1].UpdateTime);
        Assert.Equal("first second", completed[1].GetText());
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(completed[1].ToChatMessage()!));
        var replay = await fixture.Provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, []),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(["question", "first second"], replay.Select(message => message.Text));
        Assert.DoesNotContain(
            completed,
            row => row.ConversationPayload!.Contains("private memory", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    public async Task StreamingHistory_EarlyDisposal_PreservesPartialResponseAndExcludesItFromModelHistory(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        await using var fixture = await StreamingHistoryFixture.CreateAsync(mode);
        await using var capabilities = CreateCapabilities();
        var model = new PausedChatClient();
        var history = fixture.Provider;
        var agent = CreateAgent(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history,
            null
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);
        await using (
            var enumerator = ConversationHistoryPersistenceContext
                .RunStreaming(
                    agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, "question")],
                        session,
                        cancellationToken: TestContext.Current.CancellationToken
                    ),
                    fixture.Provider,
                    fixture.ProjectId,
                    "streaming",
                    0
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken)
        )
        {
            model.Emit("interrupted answer");
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Empty(await fixture.ReadAsync());
            if (mode == ConversationHistoryWriteMode.TurnEnd)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                Assert.Empty(await fixture.ReadAsync());
            }
        }
        Assert.Equal(["question", "interrupted answer"], (await fixture.ReadAsync()).Select(row => row.GetText()));
        var replay = await fixture.Provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, []),
            TestContext.Current.CancellationToken
        );
        Assert.Equal("question", Assert.Single(replay).Text);
    }

    [Fact]
    public async Task StreamingHistory_ToolLoop_PreservesInputOrderingAndModelContext()
    {
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "tool result", "lookup")]);
        var model = new PausedChatClient { CallToolFirst = true };
        var history = fixture.Provider;
        var agent = CreateAgent(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history,
            null
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);
        var execution = ConversationHistoryPersistenceContext.RunStreaming(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "question")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            ),
            fixture.Provider,
            fixture.ProjectId,
            "streaming",
            0
        );
        var draining = DrainAsync(execution);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], model.Input.Select(message => message.Role));
        Assert.Single(model.Input.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(model.Input.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        await fixture.AdvanceFlushTimerAsync();
        await fixture.WaitForCommitAsync();
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            (await fixture.ReadAsync()).Select(row => row.ToChatMessage()!.Role)
        );
        model.Emit("answer after tool");
        model.Finish();
        await draining.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var replay = await fixture.Provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, []),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant],
            replay.Select(message => message.Role)
        );
        Assert.Equal(4, (await fixture.ReadAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingHistory_ModelFailsOrCancels_PreservesPartialOutputAndOriginalFailure(bool cancel)
    {
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        await using var fixture = await StreamingHistoryFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var capabilities = CreateCapabilities();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var model = new PausedChatClient();
        var history = fixture.Provider;
        var agent = CreateAgent(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history,
            null
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);
        await using (
            var enumerator = ConversationHistoryPersistenceContext
                .RunStreaming(
                    agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, "question")],
                        session,
                        cancellationToken: cancellation.Token
                    ),
                    fixture.Provider,
                    fixture.ProjectId,
                    "streaming",
                    0
                )
                .GetAsyncEnumerator(cancellation.Token)
        )
        {
            model.Emit("partial");
            Assert.True(await enumerator.MoveNextAsync());
            var next = enumerator.MoveNextAsync().AsTask();
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            }
            else
            {
                var failure = new InvalidOperationException("model failed");
                model.Finish(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => next));
            }
        }
        var rows = await fixture.ReadAsync();
        Assert.Equal(["question", "partial"], rows.Select(row => row.GetText()));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(rows[1].ToChatMessage()!));
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> execution)
    {
        await foreach (var _ in execution.WithCancellation(TestContext.Current.CancellationToken)) { }
    }

    [Fact]
    public async Task StreamingHistory_ToolLoopDisposedDuringSecondCall_DoesNotDuplicateToolResult()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "tool result", "lookup")]);
        var model = new PausedChatClient { CallToolFirst = true };
        var agent = CreateAgent(
            model.AsAgwAgent(
                CreateDefinition(fixture.Provider),
                capabilities,
                NullLoggerFactory.Instance,
                fixture.Services
            ),
            fixture.Provider,
            null
        );
        var session = await InitializeHistorySessionAsync(agent, fixture);
        model.Emit("partial second reply");
        await using (
            var enumerator = ConversationHistoryPersistenceContext
                .RunStreaming(
                    agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, "question")],
                        session,
                        cancellationToken: TestContext.Current.CancellationToken
                    ),
                    fixture.Provider,
                    fixture.ProjectId,
                    "streaming",
                    0
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken)
        )
        {
            while (await enumerator.MoveNextAsync())
                if (enumerator.Current.Text == "partial second reply")
                    break;
        }
        var records = await fixture.ReadAsync();
        Assert.Single(records.SelectMany(record => record.ToChatMessage()!.Contents).OfType<FunctionResultContent>());
        Assert.Equal(4, records.Count);
    }

    private sealed class PausedChatClient : IChatClient
    {
        private readonly Channel<ChatResponseUpdate> _updates = Channel.CreateUnbounded<ChatResponseUpdate>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessage> Input { get; private set; } = [];
        public bool CallToolFirst { get; init; }
        public bool IncludeMessageIds { get; init; } = true;
        private int _calls;

        public void Emit(string text) =>
            _updates.Writer.TryWrite(
                new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    MessageId = IncludeMessageIds ? "answer-1" : null,
                    ResponseId = "response-1",
                }
            );

        public void Finish(Exception? failure = null) => _updates.Writer.TryComplete(failure);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Input = messages.ToList();
            if (CallToolFirst && _calls++ == 0)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]
                )
                {
                    MessageId = "tool-call",
                    ResponseId = "tool-response",
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                yield break;
            }
            Started.TrySetResult();
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
                yield return update;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StreamingHistoryFixture : DbTransactionInterceptor, IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"agw-streaming-{Guid.NewGuid():N}.db");
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly Channel<bool> _commits = Channel.CreateUnbounded<bool>();
        public ManualTimeProvider Clock { get; } = new();
        public Guid ProjectId { get; } = Guid.CreateVersion7();
        public ServiceProvider Services { get; }
        public EfCoreChatHistoryProvider Provider { get; }
        public bool FailNextContext { get; set; }

        private StreamingHistoryFixture(ConversationHistoryWriteMode mode)
        {
            _options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={_path};Pooling=False")
                .AddInterceptors(this)
                .Options;
            Services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ =>
                {
                    if (FailNextContext)
                    {
                        FailNextContext = false;
                        throw new DbUpdateException("Simulated history persistence failure.");
                    }
                    return new AgwDbContext(_options);
                })
                .BuildServiceProvider();
            Provider = new EfCoreChatHistoryProvider(
                Services.GetRequiredService<IServiceScopeFactory>(),
                InMemoryApplicationLock.Shared,
                NullLogger<EfCoreChatHistoryProvider>.Instance,
                Clock,
                options: Options.Create(new ConversationHistoryOptions { Mode = mode })
            );
        }

        public static async Task<StreamingHistoryFixture> CreateAsync(
            ConversationHistoryWriteMode mode = ConversationHistoryWriteMode.Interval
        )
        {
            var fixture = new StreamingHistoryFixture(mode);
            await using var db = new AgwDbContext(fixture._options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Projects.Add(
                new Project
                {
                    Id = fixture.ProjectId,
                    Name = "Test",
                    CreateBy = "tester",
                }
            );
            db.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = fixture.ProjectId,
                    ContextId = "streaming",
                    Title = "Chat",
                    CreateBy = "tester",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            while (fixture._commits.Reader.TryRead(out _)) { }
            return fixture;
        }

        public async Task<List<ProjectConversationChatHistory>> ReadAsync()
        {
            await using var db = new AgwDbContext(_options);
            return await db
                .ProjectConversationChatHistories.OrderBy(row => row.ConversationSequence)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            _commits.Writer.TryWrite(true);
            return Task.CompletedTask;
        }

        public async Task AdvanceFlushTimerAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            // Arm the delay before advancing virtual time; advancing between delay calculation and
            // timer registration otherwise shifts its deadline without another clock advance.
            await Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Clock.Advance(TimeSpan.FromSeconds(5));
        }

        public Task<bool> WaitForCommitAsync() =>
            _commits
                .Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            File.Delete(_path);
        }
    }
}
