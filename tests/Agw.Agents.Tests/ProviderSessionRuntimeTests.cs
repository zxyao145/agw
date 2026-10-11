using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// Runtime 的持久化绑定快照、保存失败状态与复用检查；保存与读取都经过真实的绑定持久化与 SQLite 数据库。
/// The Runtime's persisted binding snapshot, save failure state and reuse checks; saves and reads go through the real binding persistence and a SQLite database.
/// </summary>
public sealed class ProviderSessionRuntimeTests : IDisposable
{
    private const string FailBindingInsert =
        "CREATE TRIGGER fail_binding_insert BEFORE INSERT ON project_conversation_binding BEGIN SELECT RAISE(ABORT, 'binding save failed'); END;";

    private readonly IDisposable _user = ProviderSessionExecutionHarness.EnterUser();

    public void Dispose() => _user.Dispose();

    [Fact]
    public async Task IsRuntimeCurrentAsync_UnboundSession_AllowsReuse()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var state = await harness.ReadProviderSessionAsync();

        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), state);

        Assert.Null(state!.PersistedProviderSessionId);
        Assert.True(await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsRuntimeCurrentAsync_SameActiveSession_AllowsReuse()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        await harness.SaveBindingAsync("session-a");

        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());

        Assert.Equal("session-a", runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.True(await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("session-a", null)]
    [InlineData(null, "session-b")]
    [InlineData("session-a", "session-b")]
    public async Task IsRuntimeCurrentAsync_BindingIdentityChanged_RejectsReuse(string? snapshot, string? current)
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        if (snapshot != null)
        {
            await harness.SaveBindingAsync(snapshot);
        }

        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        if (current == null)
        {
            await harness.ArchiveAsync(Assert.Single(await harness.ReadBindingsAsync()).Id);
        }
        else
        {
            await harness.SaveBindingAsync(current);
        }

        Assert.Equal(snapshot, runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.False(
            await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task IsRuntimeCurrentAsync_BindingSaveFailed_RejectsReuse()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var unbound = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        await harness.SaveBindingAsync("session-a");
        await harness.SaveBindingAsync("session-b");
        var bound = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        await harness.ExecuteSqlAsync(FailBindingInsert);
        var callback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            unbound.ProviderSession,
            ProviderSessionExecutionHarness.UserId
        )!;
        var boundCallback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            bound.ProviderSession,
            ProviderSessionExecutionHarness.UserId
        )!;

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await callback("session-c", TestContext.Current.CancellationToken)
        );
        await Assert.ThrowsAsync<AgwException>(async () =>
            await boundCallback("session-a", TestContext.Current.CancellationToken)
        );

        // 两个 Runtime 的快照仍与数据库生效记录一致，失败状态单独决定不可复用。
        // Both Runtimes' snapshots still match the active database record; the failure state alone makes them non-reusable.
        Assert.Null(unbound.ProviderSession!.PersistedProviderSessionId);
        Assert.Equal("session-b", bound.ProviderSession!.PersistedProviderSessionId);
        Assert.True(unbound.ProviderSession.HasFailed);
        Assert.True(bound.ProviderSession.HasFailed);
        Assert.False(
            await harness.RuntimeFactory.IsRuntimeCurrentAsync(unbound, TestContext.Current.CancellationToken)
        );
        Assert.False(await harness.RuntimeFactory.IsRuntimeCurrentAsync(bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderSessionCallback_SaveSucceeds_UpdatesSnapshot()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        var callback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            runtime.ProviderSession,
            ProviderSessionExecutionHarness.UserId
        )!;

        await callback("  session-a  ", TestContext.Current.CancellationToken);
        await callback("session-a", TestContext.Current.CancellationToken);

        Assert.Equal("session-a", runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.False(runtime.ProviderSession.HasFailed);
        Assert.Single(harness.Commands.BindingWrites);
        var binding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.Equal("session-a", binding.ProviderSessionId);
        Assert.Null(binding.UpdateTime);
        Assert.True(await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderSessionCallback_ArchivedSession_PropagatesConflict()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        await harness.SaveBindingAsync("session-a");
        await harness.SaveBindingAsync("session-b");
        var state = await harness.ReadProviderSessionAsync();
        var callback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            state,
            ProviderSessionExecutionHarness.UserId
        )!;

        var error = await Assert.ThrowsAsync<AgwException>(async () =>
            await callback("session-a", TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        Assert.True(state!.HasFailed);
        Assert.Equal("session-b", state.PersistedProviderSessionId);
        var bindings = await harness.ReadBindingsAsync();
        Assert.False(Assert.Single(bindings, binding => binding.ProviderSessionId == "session-a").IsActive);
        Assert.True(Assert.Single(bindings, binding => binding.ProviderSessionId == "session-b").IsActive);
    }

    [Fact]
    public async Task ProviderSessionCallback_ReenteredAfterFailure_PropagatesFirstFailure()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var state = await harness.ReadProviderSessionAsync();
        var callback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            state,
            ProviderSessionExecutionHarness.UserId
        )!;
        await harness.ExecuteSqlAsync(FailBindingInsert);

        var first = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await callback("session-a", TestContext.Current.CancellationToken)
        );
        await harness.ExecuteSqlAsync("DROP TRIGGER fail_binding_insert;");
        var second = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await callback("session-b", CancellationToken.None)
        );

        Assert.Same(first, second);
        Assert.Single(harness.Commands.BindingWrites);
        Assert.Single(harness.Commands.BindingLoads);
        Assert.Null(state!.PersistedProviderSessionId);
        Assert.Empty(await harness.ReadBindingsAsync());
    }

    [Fact]
    public async Task ProviderSessionCallback_CancelledSave_MarksRuntimeNonReusable()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        var callback = harness.ProviderBindings.CreateExternalSessionStartedCallback(
            runtime.ProviderSession,
            ProviderSessionExecutionHarness.UserId
        )!;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await callback("session-a", cancelled.Token)
        );

        Assert.True(runtime.ProviderSession!.HasFailed);
        Assert.Empty(await harness.ReadBindingsAsync());
        Assert.False(
            await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ClaudeCodeTracking_InitNotification_SavesOncePerRuntime()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        var sessionId = Guid.NewGuid();
        var agent = new ClaudeCodeProviderSessionTrackingAgent(
            new InitAgent(sessionId),
            harness.ProviderBindings.CreateExternalSessionStartedCallback(
                runtime.ProviderSession,
                ProviderSessionExecutionHarness.UserId
            )
        );

        await RunAsync(agent);
        await RunAsync(agent);

        Assert.Equal(sessionId.ToString("D"), runtime.ProviderSession!.PersistedProviderSessionId);
        Assert.Single(harness.Commands.BindingLoads);
        Assert.Equal(sessionId.ToString("D"), Assert.Single(await harness.ReadBindingsAsync()).ProviderSessionId);
        Assert.True(await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClaudeCodeTracking_SaveFails_PropagatesFromRun()
    {
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Codex);
        var runtime = await harness.CreateRuntimeAsync(new IdleAgent(), await harness.ReadProviderSessionAsync());
        var agent = new ClaudeCodeProviderSessionTrackingAgent(
            new InitAgent(Guid.NewGuid()),
            harness.ProviderBindings.CreateExternalSessionStartedCallback(
                runtime.ProviderSession,
                ProviderSessionExecutionHarness.UserId
            )
        );
        await harness.ExecuteSqlAsync(FailBindingInsert);

        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(agent));

        Assert.True(runtime.ProviderSession!.HasFailed);
        Assert.Null(runtime.ProviderSession.PersistedProviderSessionId);
        Assert.False(
            await harness.RuntimeFactory.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken)
        );
    }

    private static async Task RunAsync(AIAgent agent)
    {
        await foreach (
            var _ in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        ) { }
    }

    /// <summary>
    /// 每次执行都输出一条 Claude Code 的 init 通知，与 CLI 恢复同一 session 时的行为一致。
    /// Emits a Claude Code init notification on every run, as the CLI does when it resumes the same session.
    /// </summary>
    private sealed class InitAgent : IdleAgent
    {
        private readonly Guid _sessionId;

        public InitAgent(Guid sessionId)
        {
            _sessionId = sessionId;
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.System, [])
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["subtype"] = "init",
                    ["session_id"] = _sessionId.ToString("D"),
                },
            };
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("done")]);
        }
    }

    private class IdleAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new IdleSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new IdleSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => Task.FromResult(new AgentResponse([]));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            yield break;
        }

        private sealed class IdleSession : AgentSession;
    }
}
