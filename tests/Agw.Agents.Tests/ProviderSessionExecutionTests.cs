using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

/// <summary>
/// External Agent 的 provider session 绑定在真实执行链路中的行为：SDK 通知保存生效记录，Runtime 按持久化绑定快照复用或重建，归档与 Turn 启动按对话执行锁排序。
/// Provider session bindings of External Agents in the real execution chain: SDK notifications save the active record, Runtimes are reused or rebuilt by their persisted binding snapshot, and archiving and turn starts are ordered by the conversation execution lock.
/// </summary>
public sealed class ProviderSessionExecutionTests : IDisposable
{
    private const string FailBindingInsert =
        "CREATE TRIGGER fail_binding_insert BEFORE INSERT ON project_conversation_binding BEGIN SELECT RAISE(ABORT, 'binding save failed'); END;";

    private readonly IDisposable _user = ProviderSessionExecutionHarness.EnterUser();

    public static bool CanRunFakeCli => !OperatingSystem.IsWindows();

    public static bool CanRunCodexFixture => CodexAppServerFixture.IsAvailable;

    public static bool CanRunCodexLive =>
        CanRunCodexFixture && CodexLiveTestOptions.Current is { RunProviderSessionTests: true };

    public void Dispose() => _user.Dispose();

    [Fact(SkipUnless = nameof(CanRunCodexLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task StartAsync_CodexThreadLifecycle_PersistsResumesAndReplacesArchivedThread()
    {
        var options = Assert.IsType<CodexLiveTestOptions>(CodexLiveTestOptions.Current);
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            codexOptions: options
        );

        var firstTurnId = await harness.StartTurnAsync("Reply with one word: FIRST", stream: true);
        Assert.NotNull(await harness.WaitForAssistantTextAsync(firstTurnId));
        var firstBinding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(firstBinding.IsActive);
        Assert.Equal(firstBinding.ProviderSessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
        await harness.WaitIdleAsync();
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(firstTurnId)).Status);
        var firstRuntime = harness.Runtime;
        Assert.Equal(harness.AgentName, firstBinding.ExternalAgentName);

        await harness.RebuildCoordinatorAsync();
        Assert.True(firstRuntime.IsDisposed);

        var resumed = await harness.RunTurnAsync("Reply with one word: RESUMED");
        Assert.Equal(ProjectConversationTurnStatus.Completed, resumed.Status);
        Assert.NotSame(firstRuntime, harness.Runtime);
        var resumedBinding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.Equal(firstBinding.ProviderSessionId, resumedBinding.ProviderSessionId);
        Assert.Equal(firstBinding.ProviderSessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);

        await harness.ArchiveAsync(resumedBinding.Id);
        var afterArchive = await harness.RunTurnAsync("Reply with one word: FRESH");

        Assert.Equal(ProjectConversationTurnStatus.Completed, afterArchive.Status);
        Assert.True(harness.Runtime.ProviderSession!.PersistedProviderSessionId is not null);
        var bindings = await harness.ReadBindingsAsync();
        Assert.False(Assert.Single(bindings, binding => binding.Id == firstBinding.Id).IsActive);
        var activeBinding = Assert.Single(bindings, binding => binding.IsActive);
        Assert.NotEqual(firstBinding.ProviderSessionId, activeBinding.ProviderSessionId);
        Assert.Equal(activeBinding.ProviderSessionId, harness.Runtime.ProviderSession.PersistedProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunCodexLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task StartAsync_CodexBindingSaveFails_BeforeResponseUpdates()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            codexOptions: CodexLiveTestOptions.Current
        );
        await harness.ExecuteSqlAsync(FailBindingInsert);

        var turnId = await harness.StartTurnAsync("Reply with one word: READY", stream: true);
        await harness.WaitIdleAsync();
        var failed = await harness.ReadTurnAsync(turnId);
        var failedRuntime = harness.Runtime;

        Assert.Equal(ProjectConversationTurnStatus.Failed, failed.Status);
        Assert.Contains(failed.Errors, error => error.Contains("saving the entity changes", StringComparison.Ordinal));
        Assert.Single(harness.Commands.BindingWrites);
        Assert.Single(harness.Commands.BindingLoads);
        Assert.True(failedRuntime.ProviderSession!.HasFailed);
        Assert.Null(failedRuntime.ProviderSession.PersistedProviderSessionId);
        Assert.Empty(await harness.ReadBindingsAsync());
        Assert.DoesNotContain(
            harness.ReadMessages(turnId),
            message =>
                message.Role == AiRole.Assistant
                && message.Contents.OfType<AgwTextContent>().Any(content => !string.IsNullOrWhiteSpace(content.Content))
        );
    }

    [Fact(SkipUnless = nameof(CanRunCodexLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task StartAsync_ArchiveBeforeExecutionLock_RebuildsRuntimeForNextTurn()
    {
        var gate = new PausingConversationGate();
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            gate.Wrap,
            CodexLiveTestOptions.Current
        );
        await harness.RunTurnAsync("Reply with one short word: FIRST");
        var archivedRuntime = harness.Runtime;
        var archived = Assert.Single(await harness.ReadBindingsAsync());
        var pause = gate.PauseBeforeAcquire();

        var start = Task.Run(
            () => harness.StartTurnAsync("Reply with one short word: FRESH"),
            TestContext.Current.CancellationToken
        );
        await pause.Arrived.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await harness.ArchiveAsync(archived.Id);
        pause.Continue();
        var turnId = await start;
        await harness.WaitIdleAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turnId)).Status);
        Assert.True(archivedRuntime.IsDisposed);
        Assert.NotSame(archivedRuntime, harness.Runtime);
        var bindings = await harness.ReadBindingsAsync();
        Assert.False(Assert.Single(bindings, binding => binding.Id == archived.Id).IsActive);
        var activeBinding = Assert.Single(bindings, binding => binding.IsActive);
        Assert.NotEqual(archived.ProviderSessionId, activeBinding.ProviderSessionId);
        Assert.Equal(activeBinding.ProviderSessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunCodexLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task StartAsync_TurnHoldsExecutionLock_ArchiveConflictsUntilTurnEnds()
    {
        var gate = new PausingConversationGate();
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            gate.Wrap,
            CodexLiveTestOptions.Current
        );
        await harness.RunTurnAsync("Reply with one short word: FIRST");
        var runtime = harness.Runtime;
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        var pause = gate.PauseAfterAcquire();

        var start = Task.Run(
            () => harness.StartTurnAsync("Reply with one short word: SECOND"),
            TestContext.Current.CancellationToken
        );
        await pause.Arrived.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var conflict = await Assert.ThrowsAsync<AgwException>(() => harness.ArchiveAsync(binding.Id));
        pause.Continue();
        var turnId = await start;
        await harness.WaitIdleAsync();

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, conflict.Code);
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turnId)).Status);
        Assert.Same(runtime, harness.Runtime);
        Assert.Equal(binding.ProviderSessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.True(Assert.Single(await harness.ReadBindingsAsync()).IsActive);

        await harness.ArchiveAsync(binding.Id);

        Assert.False(Assert.Single(await harness.ReadBindingsAsync()).IsActive);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_PiNewSession_NotifiesOnceAndReusesBoundSession()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Pi);

        var first = await harness.RunTurnAsync();
        var runtime = harness.Runtime;
        var second = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, first.Status);
        Assert.Equal(ProjectConversationTurnStatus.Completed, second.Status);
        Assert.Same(runtime, harness.Runtime);
        var invocation = Assert.Single(harness.Cli.PiInvocations);
        Assert.Null(invocation.Resumed);
        Assert.Single(harness.Commands.BindingLoads);
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Equal(invocation.SessionId, binding.ProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_PiExistingSession_ResumesWithoutNotification()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Pi);
        var existing = Guid.CreateVersion7().ToString("D");
        await harness.SaveBindingAsync(existing);
        var loadsBefore = harness.Commands.BindingLoads.Count;

        var outcome = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, outcome.Status);
        Assert.Equal(existing, Assert.Single(harness.Cli.PiInvocations).Resumed);
        Assert.Equal(loadsBefore, harness.Commands.BindingLoads.Count);
        Assert.Equal(existing, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.Equal(existing, Assert.Single(await harness.ReadBindingsAsync()).ProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_PiSaveFails_FailsTurnAndRebuildsRuntime()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Pi);
        await harness.ExecuteSqlAsync(FailBindingInsert);

        var failed = await harness.RunTurnAsync();
        var failedRuntime = harness.Runtime;

        Assert.Equal(ProjectConversationTurnStatus.Failed, failed.Status);
        Assert.Contains(failed.Errors, error => error.Contains("saving the entity changes", StringComparison.Ordinal));
        Assert.True(failedRuntime.ProviderSession!.HasFailed);
        Assert.Single(harness.Commands.BindingWrites);

        await harness.ExecuteSqlAsync("DROP TRIGGER fail_binding_insert;");
        var next = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, next.Status);
        Assert.True(failedRuntime.IsDisposed);
        var invocations = harness.Cli.PiInvocations;
        Assert.Equal(2, invocations.Count);
        Assert.Null(invocations[1].Resumed);
        Assert.Equal(invocations[1].SessionId, Assert.Single(await harness.ReadBindingsAsync()).ProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunCodexLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task CreateAgentflowNodeAgentAsync_CodexExternal_DoesNotSaveProviderSession()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            codexOptions: CodexLiveTestOptions.Current
        );
        var node = await harness.RuntimeFactory.CreateAgentflowNodeAgentAsync(
            harness.AgentId,
            harness.ProjectId,
            harness.ConversationId,
            environmentVariables: null,
            deferHumanInteractions: true,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(node);
        await using var nodeResource = Assert.IsAssignableFrom<IAsyncDisposable>(node.Agent);
        var session = await node.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await foreach (
            var _ in node.Agent.RunStreamingAsync(
                [
                    new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.User,
                        "Reply with one word: NODE"
                    ),
                ],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        ) { }

        Assert.Empty(await harness.ReadBindingsAsync());
        Assert.Empty(harness.Commands.BindingLoads);
    }

    [Fact]
    public async Task ReadStateAsync_SystemAgent_HasNoProviderSessionBinding()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var systemAgent = harness.Definition;
        systemAgent.Type = AgentType.System;
        systemAgent.ExternalAgentKind = EngineKind.Maf;

        var state = await harness.ProviderBindings.ReadStateAsync(
            systemAgent,
            harness.ExecutionTask,
            ProviderSessionExecutionHarness.ContextId,
            TestContext.Current.CancellationToken
        );

        Assert.Null(state);
        Assert.Null(
            harness.ProviderBindings.CreateExternalSessionStartedCallback(state, ProviderSessionExecutionHarness.UserId)
        );
    }

    /// <summary>
    /// 在真实对话执行锁之前或之后暂停一次 <c>AcquireAsync</c>，只控制执行顺序；锁本身由真实实现取得。
    /// Pauses one <c>AcquireAsync</c> before or after the real conversation execution lock, controlling only the order; the lock itself is taken by the real implementation.
    /// </summary>
    private sealed class PausingConversationGate
    {
        private Pause? _before;
        private Pause? _after;

        public IConversationExecutionGate Wrap(IConversationExecutionGate inner) => new Gate(this, inner);

        public Pause PauseBeforeAcquire() => _before = new Pause();

        public Pause PauseAfterAcquire() => _after = new Pause();

        public sealed class Pause
        {
            private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Arrived => _arrived.Task;

            public void Continue() => _continue.TrySetResult();

            public async Task WaitAsync(CancellationToken cancellationToken)
            {
                _arrived.TrySetResult();
                await _continue.Task.WaitAsync(cancellationToken);
            }
        }

        private sealed class Gate : IConversationExecutionGate
        {
            private readonly PausingConversationGate _owner;
            private readonly IConversationExecutionGate _inner;

            public Gate(PausingConversationGate owner, IConversationExecutionGate inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public async Task<IApplicationLockLease> AcquireAsync(
                Guid conversationId,
                int expectedGeneration,
                CancellationToken cancellationToken = default
            )
            {
                if (Interlocked.Exchange(ref _owner._before, null) is { } before)
                {
                    await before.WaitAsync(cancellationToken);
                }

                var lease = await _inner.AcquireAsync(conversationId, expectedGeneration, cancellationToken);
                if (Interlocked.Exchange(ref _owner._after, null) is { } after)
                {
                    await after.WaitAsync(cancellationToken);
                }

                return lease;
            }
        }
    }
}
