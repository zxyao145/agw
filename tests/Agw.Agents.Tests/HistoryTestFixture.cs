using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
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

/// <summary>
/// 历史测试使用的 SQLite 数据库、真实的 ConversationHistoryStore 与 Turn 执行作用域。
/// The SQLite database, real ConversationHistoryStore and turn execution scope used by history tests.
/// </summary>
internal sealed class HistoryTestFixture : DbTransactionInterceptor, IAsyncDisposable
{
    public const string ContextId = "streaming";
    public const string UserId = "tester";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"agw-history-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<AgwDbContext> _options;
    private readonly Channel<bool> _commits = Channel.CreateUnbounded<bool>();
    private int _commitCount;

    private HistoryTestFixture(ConversationHistoryWriteMode mode, long capacity)
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
                    ContextFailure.TrySetResult();
                    throw new DbUpdateException("Simulated history persistence failure.");
                }
                return new AgwDbContext(_options);
            })
            .BuildServiceProvider();
        Store = new ConversationHistoryStore(
            Services.GetRequiredService<IServiceScopeFactory>(),
            InMemoryApplicationLock.Shared,
            NullLogger<ConversationHistoryStore>.Instance,
            Clock,
            options: Options.Create(new ConversationHistoryOptions { Mode = mode, MaxBufferedBytes = capacity })
        );
    }

    /// <summary>
    /// 数据库事务提交的次数。
    /// The number of committed database transactions.
    /// </summary>
    public int CommitCount => Volatile.Read(ref _commitCount);

    /// <summary>
    /// 为真时下一次提交成功后抛出异常，模拟提交确认丢失。
    /// When true the next successful commit throws afterwards, simulating a lost commit acknowledgement.
    /// </summary>
    public bool ThrowAfterCommit { get; set; }

    public TaskCompletionSource ContextFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualTimeProvider Clock { get; } = new();

    public Guid ProjectId { get; } = Guid.CreateVersion7();

    public Guid ConversationId { get; } = Guid.CreateVersion7();

    public ServiceProvider Services { get; }

    public ConversationHistoryStore Store { get; }

    public HistorySessionState SessionState { get; } = new();

    public bool FailNextContext { get; set; }

    public static async Task<HistoryTestFixture> CreateAsync(
        ConversationHistoryWriteMode mode = ConversationHistoryWriteMode.Interval,
        long capacity = 16 * 1024 * 1024
    )
    {
        var fixture = new HistoryTestFixture(mode, capacity);
        await using var db = new AgwDbContext(fixture._options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.Projects.Add(
            new Project
            {
                Id = fixture.ProjectId,
                Name = "Test",
                CreateBy = UserId,
            }
        );
        db.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = fixture.ConversationId,
                ProjectId = fixture.ProjectId,
                ContextId = ContextId,
                Title = "Chat",
                CreateBy = UserId,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        while (fixture._commits.Reader.TryRead(out _)) { }
        Volatile.Write(ref fixture._commitCount, 0);
        return fixture;
    }

    public static IDisposable EnterUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"))
        );

    public AgwChatHistoryProvider CreateProvider(EngineKind engine = EngineKind.Maf, bool structuredResult = false) =>
        AgwChatHistoryProvider.Create(Store, SessionState, Clock, engine, structuredResult);

    /// <summary>
    /// 用历史记录包住一个 Agent；memoryText 不为空时每个 Turn 注入一次记忆上下文。
    /// Wraps an Agent with history recording; a non-null memoryText injects memory context once per turn.
    /// </summary>
    public static HistoryRecordingAgent Record(
        AIAgent innerAgent,
        AgwChatHistoryProvider history,
        string? memoryText = null
    ) =>
        new(
            innerAgent,
            history,
            memoryText == null
                ? null
                : _ =>
                    ValueTask.FromResult<ChatMessage?>(
                        new ChatMessage(ChatRole.User, memoryText).WithAgentRequestMessageSource(
                            AgentRequestMessageSourceType.AIContextProvider,
                            ConversationHistoryMetadata.UserMemorySourceId
                        )
                    ),
            NullLogger.Instance
        );

    /// <summary>
    /// 直接创建一次运行的历史记录，由测试按 SDK 的回调顺序驱动。
    /// Creates the history recording of one run directly, driven by the test in the SDK's callback order.
    /// </summary>
    public HistoryRecording CreateRecording(IAgentMessageAdapter<AgentResponseUpdate> adapter) =>
        new(
            new ConversationMessageWriteScope
            {
                ProjectId = ProjectId,
                ContextId = ContextId,
                Generation = 0,
                ProducerId = Guid.CreateVersion7(),
            },
            Store,
            adapter,
            Clock,
            "test",
            transient: false,
            structuredResult: false
        );

    public async Task<List<ChatMessage>> ReadMessagesAsync() =>
        (await ReadAsync()).Select(row => row.ToChatMessage()!).ToList();

    public async Task<AgentSession> CreateSessionAsync(AIAgent agent)
    {
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        SessionState.InitializeSessionState(session, ContextId, ProjectId);
        return session;
    }

    /// <summary>
    /// 建立一个 Agent Turn 的执行作用域并绑定本对话的历史缓冲；方法同步执行，作用域留在调用方的异步上下文中。
    /// ownershipLost 取消时表示本实例失去执行所属。
    /// Pushes the execution scope of one Agent turn and binds this conversation's history buffer; the method is synchronous so the scope stays in the caller's async context.
    /// Cancelling ownershipLost means this instance lost execution ownership.
    /// </summary>
    public HistoryTurn BeginTurn(
        int generation = 0,
        ITurnCheckpointStore? checkpoints = null,
        CancellationTokenSource? ownershipLost = null,
        PendingInteractionSet? interactions = null
    )
    {
        var context = ExecutionTestScopes.Context(
            projectId: ProjectId,
            contextId: ContextId,
            userId: UserId,
            generation: generation,
            conversationId: ConversationId
        );
        var scope = ExecutionScope.Create(
            context,
            Guid.CreateVersion7(),
            new InteractionTestSink(),
            interactions ?? new InMemoryPendingInteractionSet(context.PermissionMode),
            checkpoints
        );
        var pushed = scope.Push();
        var buffer = Store.BeginBuffer(Conversation(generation), ownershipLost?.Token ?? CancellationToken.None);
        scope.BindHistory(buffer);
        return new HistoryTurn(scope, buffer, pushed);
    }

    /// <summary>
    /// 建立属于本对话、还没有历史缓冲的执行作用域，由 TurnHistory 为它建立缓冲。
    /// Pushes an execution scope of this conversation without a history buffer, so TurnHistory creates one for it.
    /// </summary>
    public (ExecutionScope Scope, IDisposable Pushed) PushUnboundScope()
    {
        var context = ExecutionTestScopes.Context(
            projectId: ProjectId,
            contextId: ContextId,
            userId: UserId,
            conversationId: ConversationId
        );
        var scope = ExecutionScope.Create(
            context,
            Guid.CreateVersion7(),
            new InteractionTestSink(),
            new InMemoryPendingInteractionSet(context.PermissionMode)
        );
        return (scope, scope.Push());
    }

    public ConversationHistoryScope Conversation(int generation = 0) =>
        new()
        {
            ProjectId = ProjectId,
            ContextId = ContextId,
            Generation = generation,
            IsExecutionBound = true,
        };

    public ConversationHistoryWriter CreateWriter() => new(Store, Clock);

    /// <summary>
    /// 按模型调用前的规则读取模型历史的文本，不产生写入。
    /// Reads the texts of model history by the pre-call rules, without writing anything.
    /// </summary>
    public async Task<string[]> ReadModelTextsAsync(string? historyScope = null)
    {
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        if (historyScope == null)
            SessionState.InitializeSessionState(session, ContextId, ProjectId);
        else
            SessionState.InitializeSessionState(session, ContextId, ProjectId, historyScope);
        var method = typeof(AgwChatHistoryProvider).GetMethod(
            "ProvideChatHistoryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        var messages = await (ValueTask<IEnumerable<ChatMessage>>)
            method.Invoke(
                CreateProvider(),
                [new ChatHistoryProvider.InvokingContext(agent, session, []), TestContext.Current.CancellationToken]
            )!;
        return messages.Select(message => message.Text).ToArray();
    }

    public AgwDbContext CreateContext() => new(_options);

    /// <summary>
    /// 按模型调用前的方式读取模型历史，再结束这次读取建立的临时记录。
    /// Reads model history the way a model call does, then ends the transient recording this read created.
    /// </summary>
    public static async Task<IReadOnlyList<ChatMessage>> ReplayAsync(
        AgwChatHistoryProvider history,
        AIAgent agent,
        AgentSession session
    )
    {
        var messages = await history.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, []),
            TestContext.Current.CancellationToken
        );
        await history.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, [], []),
            TestContext.Current.CancellationToken
        );
        return messages.ToList();
    }

    /// <summary>
    /// 按 SDK 的回调顺序驱动一次运行：暂存原始输入，模型调用前的读取写入输入，随后逐批交出完整响应。
    /// Drives one run in the SDK's callback order: stages the original input, the pre-call read writes it, then complete responses are handed over batch by batch.
    /// </summary>
    public static async Task RunSdkCallbacksAsync(
        AgwChatHistoryProvider history,
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> input,
        params IReadOnlyList<ChatMessage>[] responses
    )
    {
        var token = TestContext.Current.CancellationToken;
        var recording = history.Begin(agent, session);
        try
        {
            recording.Stage(input);
            await history.InvokingAsync(new ChatHistoryProvider.InvokingContext(agent, session, []), token);
            foreach (var batch in responses)
                await history.InvokedAsync(new ChatHistoryProvider.InvokedContext(agent, session, [], batch), token);
            await recording.FinishAsync(completed: true, token);
        }
        finally
        {
            history.End(session);
        }
    }

    public async Task<List<ProjectConversationChatHistory>> ReadAsync()
    {
        await using var db = new AgwDbContext(_options);
        return await db
            .ProjectConversationChatHistories.OrderBy(row => row.ConversationSequence)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async Task SetGenerationAsync(int generation)
    {
        await using var db = new AgwDbContext(_options);
        await db
            .ProjectConversations.Where(conversation => conversation.Id == ConversationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(conversation => conversation.Generation, generation),
                TestContext.Current.CancellationToken
            );
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _commitCount);
        _commits.Writer.TryWrite(true);
        if (ThrowAfterCommit)
        {
            ThrowAfterCommit = false;
            throw new DbUpdateException("Simulated lost commit acknowledgement.");
        }
        return Task.CompletedTask;
    }

    public async Task AdvanceFlushTimerAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // 先等待计时器登记，再推进虚拟时间；否则截止时间会随推进一起后移。
        // Wait for the timer registration before advancing virtual time; otherwise the deadline moves with the advance.
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

/// <summary>
/// 像 SDK 一样调用历史 Provider 的 Agent：默认在输出之后交出完整响应，也可以在输出之前交出。
/// An Agent that calls the history provider as SDKs do: by default it hands over the complete response after its output, or optionally before.
/// </summary>
internal sealed class HistoryNotifyingAgent : AIAgent
{
    private readonly ChatHistoryProvider? _historyProvider;
    private readonly Exception? _invokeException;
    private readonly bool _notifyBeforeYield;

    public HistoryNotifyingAgent(
        ChatHistoryProvider? historyProvider,
        Exception? invokeException = null,
        bool notifyBeforeYield = false
    )
    {
        _historyProvider = historyProvider;
        _invokeException = invokeException;
        _notifyBeforeYield = notifyBeforeYield;
    }

    public IReadOnlyList<ChatMessage> RequestMessages { get; private set; } = [];

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new TestSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement sessionState,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<AgentSession>(new TestSession());

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken
    )
    {
        RequestMessages = messages.ToList();
        var response = new ChatMessage(ChatRole.Assistant, "answer");
        if (_historyProvider != null)
        {
            var context =
                _invokeException == null
                    ? new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, [response])
                    : new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, _invokeException);
            await _historyProvider.InvokedAsync(context, cancellationToken);
        }
        if (_invokeException != null)
            throw _invokeException;
        return new AgentResponse([response]);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        RequestMessages = messages.ToList();
        var response = new ChatMessage(ChatRole.Assistant, "answer");
        if (_historyProvider != null && _notifyBeforeYield)
            await NotifyAsync(session, response, cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "answer");
        if (_historyProvider != null && !_notifyBeforeYield)
            await NotifyAsync(session, response, cancellationToken);
    }

    private ValueTask NotifyAsync(AgentSession? session, ChatMessage response, CancellationToken cancellationToken) =>
        _historyProvider!.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, [response]),
            cancellationToken
        );

    private sealed class TestSession : AgentSession;
}

/// <summary>
/// 一个测试 Turn：释放时恢复执行作用域，并按执行异常结束历史缓冲。
/// One test turn: disposing restores the execution scope and completes the history buffer with the execution failure.
/// </summary>
internal sealed class HistoryTurn : IAsyncDisposable
{
    private readonly IDisposable _pushed;

    public HistoryTurn(ExecutionScope scope, IConversationHistoryBuffer buffer, IDisposable pushed)
    {
        Scope = scope;
        Buffer = buffer;
        _pushed = pushed;
    }

    public ExecutionScope Scope { get; }

    public IConversationHistoryBuffer Buffer { get; }

    /// <summary>
    /// 同步恢复作用域，恢复结果留在调用方的异步上下文中；缓冲使用自己保存的身份提交。
    /// Restores the scope synchronously so the restoration stays in the caller's async context; the buffer commits with its own saved identity.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _pushed.Dispose();
        return Buffer.CompleteAsync(Scope.Failure);
    }
}
