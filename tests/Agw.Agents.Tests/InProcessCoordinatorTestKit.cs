using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// 用真实的进程内协调器、AgentTurnExecutor 与 TurnPipeline 驱动连接与 Facade；只有模型调用由 TurnAgent 代替。
/// Drives connections and the Facade with the real in-process coordinator, AgentTurnExecutor and TurnPipeline; only the model call is replaced by TurnAgent.
/// </summary>
internal sealed class InProcessCoordinatorTestKit : IAgwFileSystemResolver
{
    public TestAgentRuntimeFactory Runtimes { get; } = new();

    /// <summary>
    /// 协调器的 Turn 输出与 Turn 行来自持久化测试环境：受理时建立的广播与真实的 Turn 存储。
    /// The coordinator's turn output and turn row come from the persistence test kit: the broadcast created at acceptance and the real turn store.
    /// </summary>
    public InProcessExecutionCoordinatorFactory CreateFactory(
        ExecutionContextFactory executionContexts,
        TurnPersistenceTestKit persistence,
        IConversationExecutionGate? conversationGate = null
    ) =>
        new(
            Runtimes,
            new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance),
            new AgentflowTurnExecutor(null!, null!, NullLogger<AgentflowTurnExecutor>.Instance),
            executionContexts,
            this,
            persistence.Broadcasts,
            persistence.Services.GetRequiredService<IServiceScopeFactory>(),
            conversationGate
        );

    public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
        Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(AppContext.BaseDirectory));

    public Task<IAgwFileSystem?> ResolveSnapshotAsync(
        Guid projectId,
        ProjectWorkspaceSnapshot snapshot,
        Guid? directoryId,
        CancellationToken ct
    ) => Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(snapshot.Workspace));
}

/// <summary>
/// 为每个 Agent Definition 构造绑定到对话的 AgentRuntime，模型调用由 TurnAgent 完成，外层是真实的审批批次层。
/// Builds conversation-bound AgentRuntimes for each Agent definition, with TurnAgent performing the model call inside the real approval batch layer.
/// </summary>
internal sealed class TestAgentRuntimeFactory : IAgentRuntimeFactory
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 为真时模型调用等待 ReleaseHeldTurns 或取消。
    /// When true, the model call waits for ReleaseHeldTurns or cancellation.
    /// </summary>
    public bool HoldTurnOpen { get; set; }

    public List<AgentRuntime> Created { get; } = [];

    public List<TurnAgent> Agents { get; } = [];

    public List<string> ModeChanges { get; } = [];

    public List<AgentExecutionContext> TurnContexts { get; } = [];

    /// <summary>
    /// 为真时不创建 Runtime，模拟 Agent Definition 无法构造。
    /// When true, no Runtime is created, as when the Agent definition cannot be built.
    /// </summary>
    public bool CreatesNoRuntime { get; set; }

    /// <summary>
    /// 为真时模型响应一个需要人工决定的工具审批请求。
    /// When true, the model responds with a tool approval request that needs a human decision.
    /// </summary>
    public bool RequestsApproval { get; set; }

    /// <summary>
    /// 每次模型调用开始时释放一次。
    /// Released once whenever a model call starts.
    /// </summary>
    public SemaphoreSlim TurnStarts { get; } = new(0);

    internal Task HeldTurn => _release.Task;

    public void ReleaseHeldTurns()
    {
        _release.TrySetResult();
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        if (CreatesNoRuntime)
        {
            return null;
        }
        var agent = new TurnAgent(this);
        // 工具审批经过真实的 Agent 管线批次层，与 System Agent 相同。
        // Tool approvals go through the real Agent-pipeline batch layer, as for a System Agent.
        var pipeline = new MafApprovalBatchAgent(
            agent,
            new HumanInteractionContextAccessor(new AgentExecutionContextAccessor()),
            []
        );
        var contextId = ContextIdUtil.ResolveContextId(
            string.IsNullOrWhiteSpace(settings.ContextId) ? task.ContextId : settings.ContextId
        );
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            pipeline,
            await pipeline.CreateSessionAsync(cancellationToken),
            task.ProjectId,
            contextId,
            new AgentSessionStateScope(
                task.ProjectConversationId,
                task.ProjectId,
                contextId,
                agentId,
                generation: task.Generation
            )
        );
        Agents.Add(agent);
        Created.Add(runtime);
        return runtime;
    }

    public Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default) =>
        Task.FromResult(!runtime.IsDisposed);

    public Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default)
    {
        ModeChanges.Add(mode);
        return Task.CompletedTask;
    }

    public Task<AgentflowNodeAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool deferHumanInteractions,
        CancellationToken cancellationToken = default,
        AgwPermissionMode? permissionMode = null
    ) => throw new NotSupportedException();
}

/// <summary>
/// 记录每次调用收到的执行数据并回答 "done"；按工厂设置等待放行或取消。
/// Records the execution data of each call and answers "done"; waits for release or cancellation when the factory asks.
/// </summary>
internal sealed class TurnAgent : AIAgent, IAsyncDisposable
{
    private readonly TestAgentRuntimeFactory _owner;
    private readonly TaskCompletionSource _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TurnAgent(TestAgentRuntimeFactory owner)
    {
        _owner = owner;
    }

    public bool Disposed { get; private set; }

    public bool Canceled => _cancellation.Task.IsCompleted;

    /// <summary>
    /// 模型调用观察到取消时完成。
    /// Completes when the model call observes cancellation.
    /// </summary>
    public Task Cancellation => _cancellation.Task;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new TurnAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement sessionState,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<AgentSession>(new TurnAgentSession());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        _owner.TurnContexts.Add(
            ExecutionRunOptions.Read(options)
                ?? throw new InvalidOperationException("The Agent call carries no execution data.")
        );
        _owner.TurnStarts.Release();
        if (_owner.HoldTurnOpen)
        {
            try
            {
                await _owner.HeldTurn.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancellation.TrySetResult();
                throw;
            }
        }

        if (
            _owner.RequestsApproval
            && !messages.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>().Any()
        )
        {
            yield return new AgentResponseUpdate(
                ChatRole.Assistant,
                [
                    new ToolApprovalRequestContent(
                        "approval-1",
                        new FunctionCallContent("call-1", "run_shell", new Dictionary<string, object?>())
                    ),
                ]
            );
            yield break;
        }

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done")
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
        };
    }

    private sealed class TurnAgentSession : AgentSession;
}
