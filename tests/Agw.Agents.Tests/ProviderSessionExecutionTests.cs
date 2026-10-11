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

    public void Dispose() => _user.Dispose();

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_CodexNewThread_SavesBindingAndReusesRuntime()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);

        var first = await harness.RunTurnAsync();
        var runtime = harness.Runtime;
        var second = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, first.Status);
        Assert.Equal(ProjectConversationTurnStatus.Completed, second.Status);
        Assert.Same(runtime, harness.Runtime);
        var invocations = harness.Cli.CodexInvocations;
        Assert.Equal(2, invocations.Count);
        Assert.Null(invocations[0].Resumed);
        Assert.Equal(invocations[0].SessionId, invocations[1].Resumed);
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Equal(invocations[0].SessionId, binding.ProviderSessionId);
        Assert.Equal(harness.AgentName, binding.ExternalAgentName);
        Assert.Null(binding.UpdateTime);
        Assert.Empty(harness.Commands.BindingWrites.Where(command => command.Contains("UPDATE")));
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_AfterArchive_RebuildsRuntimeAndStartsNewThread()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        await harness.RunTurnAsync("first question before archive");
        var archivedRuntime = harness.Runtime;
        var archived = Assert.Single(await harness.ReadBindingsAsync());

        await harness.ArchiveAsync(archived.Id);
        var next = await harness.RunTurnAsync("second question after archive");

        Assert.Equal(ProjectConversationTurnStatus.Completed, next.Status);
        Assert.True(archivedRuntime.IsDisposed);
        Assert.NotSame(archivedRuntime, harness.Runtime);
        var invocations = harness.Cli.CodexInvocations;
        Assert.Equal(2, invocations.Count);
        Assert.Null(invocations[1].Resumed);
        Assert.NotEqual(invocations[0].SessionId, invocations[1].SessionId);
        // 新 session 只收到本次输入，该 Agent 之前的对话不会重放。
        // The new session receives only this input; the Agent's earlier conversation is not replayed.
        Assert.Contains("second question after archive", invocations[1].Input, StringComparison.Ordinal);
        Assert.DoesNotContain("first question before archive", invocations[1].Input, StringComparison.Ordinal);
        Assert.DoesNotContain("reply from", invocations[1].Input, StringComparison.Ordinal);
        var bindings = await harness.ReadBindingsAsync();
        Assert.False(Assert.Single(bindings, binding => binding.Id == archived.Id).IsActive);
        Assert.Equal(invocations[1].SessionId, Assert.Single(bindings, binding => binding.IsActive).ProviderSessionId);
        Assert.Equal(invocations[1].SessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_CodexResumeWithoutThreadStarted_SavesIdempotentlyAtRunEnd()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var existing = Guid.CreateVersion7().ToString("D");
        await harness.SaveBindingAsync(existing);
        harness.Cli.Configure(threadStartedOnResume: false);
        var loadsBefore = harness.Commands.BindingLoads.Count;

        var outcome = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, outcome.Status);
        Assert.Equal(existing, Assert.Single(harness.Cli.CodexInvocations).Resumed);
        // 恢复的 thread 没有 ThreadStartedEvent，SDK 在执行结束时用已知的 thread ID 通知一次。
        // The resumed thread has no ThreadStartedEvent, so the SDK notifies once with the known thread ID at the end of the run.
        Assert.Equal(loadsBefore + 1, harness.Commands.BindingLoads.Count);
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Equal(existing, binding.ProviderSessionId);
        Assert.Null(binding.UpdateTime);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_CodexSaveFails_FailsTurnOnceAndRebuildsRuntime()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        await harness.ExecuteSqlAsync(FailBindingInsert);

        var failed = await harness.RunTurnAsync();
        var failedRuntime = harness.Runtime;

        Assert.Equal(ProjectConversationTurnStatus.Failed, failed.Status);
        Assert.Contains(failed.Errors, error => error.Contains("saving the entity changes", StringComparison.Ordinal));
        // 通知在事件阶段失败后，SDK 在结束阶段再次通知；回调直接传播首次异常，数据库只收到一次写入。
        // After the event-time notification fails the SDK notifies again at the end; the callback propagates the first exception and the database sees one write.
        Assert.Single(harness.Commands.BindingWrites);
        Assert.Single(harness.Commands.BindingLoads);
        Assert.True(failedRuntime.ProviderSession!.HasFailed);
        Assert.Null(failedRuntime.ProviderSession.PersistedProviderSessionId);
        Assert.Empty(await harness.ReadBindingsAsync());

        await harness.ExecuteSqlAsync("DROP TRIGGER fail_binding_insert;");
        var next = await harness.RunTurnAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, next.Status);
        Assert.True(failedRuntime.IsDisposed);
        Assert.NotSame(failedRuntime, harness.Runtime);
        var invocations = harness.Cli.CodexInvocations;
        Assert.Equal(2, invocations.Count);
        Assert.Null(invocations[1].Resumed);
        Assert.Equal(invocations[1].SessionId, Assert.Single(await harness.ReadBindingsAsync()).ProviderSessionId);
    }

    [Theory(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_InterruptedCodexResume_SaveAtRunEndDecidesOutcome(bool saveFails)
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var existing = Guid.CreateVersion7().ToString("D");
        await harness.SaveBindingAsync(existing);
        harness.Cli.Configure(threadStartedOnResume: false, hold: true);

        var turnId = await harness.StartTurnAsync();
        await harness.Cli.WaitForCodexRunsAsync(1);
        if (saveFails)
        {
            await harness.ExecuteSqlAsync(
                "ALTER TABLE project_conversation_binding RENAME TO project_conversation_binding_hidden;"
            );
        }

        harness.Host.RequestInterrupt();
        await harness.WaitIdleAsync();
        var outcome = await harness.ReadTurnAsync(turnId);
        var runtime = harness.Runtime;
        if (saveFails)
        {
            await harness.ExecuteSqlAsync(
                "ALTER TABLE project_conversation_binding_hidden RENAME TO project_conversation_binding;"
            );
        }

        Assert.Equal(existing, Assert.Single(harness.Cli.CodexInvocations).Resumed);
        if (saveFails)
        {
            Assert.Equal(ProjectConversationTurnStatus.Failed, outcome.Status);
            Assert.Contains(outcome.Errors, error => error.Contains("no such table", StringComparison.Ordinal));
            Assert.True(runtime.ProviderSession!.HasFailed);
        }
        else
        {
            Assert.Equal(ProjectConversationTurnStatus.Interrupted, outcome.Status);
            Assert.False(runtime.ProviderSession!.HasFailed);
        }

        var binding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Equal(existing, binding.ProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_ArchiveBeforeExecutionLock_RebuildsRuntimeForNextTurn()
    {
        var gate = new PausingConversationGate();
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex, gate.Wrap);
        await harness.RunTurnAsync();
        var archivedRuntime = harness.Runtime;
        var archived = Assert.Single(await harness.ReadBindingsAsync());
        var pause = gate.PauseBeforeAcquire();

        var start = Task.Run(() => harness.StartTurnAsync(), TestContext.Current.CancellationToken);
        await pause.Arrived.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await harness.ArchiveAsync(archived.Id);
        pause.Continue();
        var turnId = await start;
        await harness.WaitIdleAsync();

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turnId)).Status);
        Assert.True(archivedRuntime.IsDisposed);
        Assert.NotSame(archivedRuntime, harness.Runtime);
        var invocations = harness.Cli.CodexInvocations;
        Assert.Null(invocations[1].Resumed);
        var bindings = await harness.ReadBindingsAsync();
        Assert.False(Assert.Single(bindings, binding => binding.Id == archived.Id).IsActive);
        Assert.Equal(invocations[1].SessionId, Assert.Single(bindings, binding => binding.IsActive).ProviderSessionId);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_TurnHoldsExecutionLock_ArchiveConflictsUntilTurnEnds()
    {
        var gate = new PausingConversationGate();
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex, gate.Wrap);
        await harness.RunTurnAsync();
        var runtime = harness.Runtime;
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        var pause = gate.PauseAfterAcquire();

        var start = Task.Run(() => harness.StartTurnAsync(), TestContext.Current.CancellationToken);
        await pause.Arrived.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var conflict = await Assert.ThrowsAsync<AgwException>(() => harness.ArchiveAsync(binding.Id));
        pause.Continue();
        var turnId = await start;
        await harness.WaitIdleAsync();

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, conflict.Code);
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turnId)).Status);
        Assert.Same(runtime, harness.Runtime);
        Assert.Equal(binding.ProviderSessionId, harness.Cli.CodexInvocations[1].Resumed);
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

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task CreateAgentflowNodeAgentAsync_ExternalAgent_DoesNotSaveProviderSession()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var node = await harness.RuntimeFactory.CreateAgentflowNodeAgentAsync(
            harness.AgentId,
            harness.ProjectId,
            harness.ConversationId,
            environmentVariables: null,
            deferHumanInteractions: true,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(node);
        var session = await node.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await foreach (
            var _ in node.Agent.RunStreamingAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "node input")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        ) { }

        Assert.Null(Assert.Single(harness.Cli.CodexInvocations).Resumed);
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
