using System.Security.Claims;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Context;

/// <summary>
/// Host 执行作用域：执行数据槽中的实际对象，持有一次 Agent 或节点调用的执行数据与进程内服务。
/// The Host execution scope: the object held by the execution slot, carrying the data and in-process services of one Agent or node call.
/// </summary>
/// <remarks>
/// 只有 Execution 能创建它。身份与权限数据在作用域内保持不变；Step 进度由每个作用域自己推进。
/// Only Execution creates it. Identity and permission data never change inside a scope; each scope advances its own Step progress.
/// </remarks>
internal sealed class ExecutionScope : IExecutionIdentity
{
    private int _stepIndex;
    private int _interactionsBound;
    private IConversationHistoryBuffer? _history;
    private Exception? _failure;
    private int _memoryInjected;

    private readonly TurnRecord? _turn;
    private readonly IExecutionWriteGuard? _writeGuard;

    private ExecutionScope(
        AgentExecutionContext context,
        Guid? taskId,
        IExecutionMessageSink output,
        PendingInteractionSet interactions,
        ITurnCheckpointStore? checkpoints,
        ExecutionScope? parent,
        TurnRecord? turn,
        IExecutionWriteGuard? writeGuard
    )
    {
        Context = context;
        TaskId = taskId;
        Output = output;
        Interactions = interactions;
        Checkpoints = checkpoints;
        Parent = parent;
        _turn = turn;
        _writeGuard = writeGuard;
        Permissions = new InteractionPermissionState(
            context.PermissionMode,
            context.ProjectConversationId,
            context.PermissionVersion
        );
    }

    public static ExecutionScope? Current => ExecutionContextSlot.Current as ExecutionScope;

    public static ExecutionScope Required => Current ?? throw new AgwException(ErrorCodes.ExecutionContextMissing);

    public AgentExecutionContext Context { get; }

    /// <summary>
    /// 本轮所属的现有 Task（内部分组）。
    /// The existing Task (internal grouping) that owns this turn.
    /// </summary>
    public Guid? TaskId { get; }

    public ExecutionScope? Parent { get; }

    /// <summary>
    /// Turn 的消息输出。
    /// The turn's message output.
    /// </summary>
    public IExecutionMessageSink Output { get; }

    public PendingInteractionSet Interactions { get; }

    /// <summary>
    /// Agent Turn 的 Step 存档；没有存档服务的作用域不保存存档。
    /// The Step checkpoints of the Agent turn; a scope without a checkpoint service saves none.
    /// </summary>
    public ITurnCheckpointStore? Checkpoints { get; }

    public InteractionPermissionState Permissions { get; }

    /// <summary>
    /// 本 Turn 的 Turn 行与受理时写入的输入行；子作用域沿用所属 Turn 的记录。
    /// This turn's row and the input row written at acceptance; child scopes use their turn's record.
    /// </summary>
    public TurnRecord? Turn => Parent?.Turn ?? _turn;

    /// <summary>
    /// Durable 执行的写入入口：历史、事件、存档与 Turn 行都经它在租约检查事务中提交；进程内为空。
    /// The write entry of a durable execution: history, events, checkpoints and the turn row commit through it in lease-checked transactions; null in-process.
    /// </summary>
    public IExecutionWriteGuard? WriteGuard => Parent?.WriteGuard ?? _writeGuard;

    /// <summary>
    /// 工具审批与 HumanGate 请求的决定来源：进程内在内存等待回答，Durable 返回等待边界。
    /// The decision source for tool approvals and HumanGate requests: in-process waits in memory, Durable returns a wait boundary.
    /// </summary>
    public IInteractionHandler? InteractionHandler { get; private set; }

    public IHumanInteractionChannel? InteractionChannel { get; private set; }

    public IInteractionRequestRegistry? InteractionRequests { get; private set; }

    /// <summary>
    /// 本 Turn 的历史缓冲；节点与后台 Agent 的子作用域沿用所属 Turn 的缓冲。
    /// This turn's history buffer; node and background Agent child scopes use the buffer of their turn.
    /// </summary>
    public IConversationHistoryBuffer? History => Parent?.History ?? Volatile.Read(ref _history);

    /// <summary>
    /// 本 Turn 执行中第一个未处理的异常；历史缓冲结束时据此保留执行异常。
    /// The first unhandled exception of this turn's execution; the history buffer keeps the execution failure when it completes.
    /// </summary>
    public Exception? Failure => Parent?.Failure ?? Volatile.Read(ref _failure);

    /// <summary>
    /// TurnExecutor 在消息流结束时报告的结局；异常结束或被取消时为空。
    /// The outcome reported by the TurnExecutor when its message stream ends; null after an exception or cancellation.
    /// </summary>
    public TurnOutcome? Outcome { get; private set; }

    public int StepIndex => Volatile.Read(ref _stepIndex);

    public string UserId => Context.UserId;

    public Guid ProjectId => Context.ProjectId;

    public Guid ProjectConversationId => Context.ProjectConversationId;

    public string ContextId => Context.ContextId;

    public int Generation => Context.Generation;

    public ProjectWorkspaceSnapshot WorkspaceSnapshot => Context.WorkspaceSnapshot;

    public static ExecutionScope Create(
        AgentExecutionContext context,
        Guid? taskId,
        IExecutionMessageSink output,
        PendingInteractionSet interactions,
        ITurnCheckpointStore? checkpoints = null,
        TurnRecord? turn = null,
        IExecutionWriteGuard? writeGuard = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(interactions);
        if (string.IsNullOrWhiteSpace(context.UserId))
            throw new AgwException(ErrorCodes.AuthenticationRequired, "A stable user id is required.");
        return new ExecutionScope(context, taskId, output, interactions, checkpoints, parent: null, turn, writeGuard);
    }

    /// <summary>
    /// 推进本作用域的 Step，返回新的 Step 序号。
    /// Advances this scope's Step and returns the new Step index.
    /// </summary>
    public int AdvanceStep() => Interlocked.Increment(ref _stepIndex);

    /// <summary>
    /// 为本 Turn 绑定历史缓冲；每个 Turn 只绑定一次，子作用域不能单独绑定。
    /// Binds this turn's history buffer; each turn binds once and child scopes cannot bind their own.
    /// </summary>
    public void BindHistory(IConversationHistoryBuffer history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (Parent != null)
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "A child execution scope uses its turn's history.");
        if (Interlocked.CompareExchange(ref _history, history, null) != null)
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "The execution scope already has a history buffer."
            );
    }

    /// <summary>
    /// 记录执行中第一个未处理的异常。
    /// Records the first unhandled exception of the execution.
    /// </summary>
    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Parent != null)
            Parent.RecordFailure(exception);
        else
            Interlocked.CompareExchange(ref _failure, exception, null);
    }

    /// <summary>
    /// 本 Turn 第一次调用时返回 true，之后返回 false；记忆上下文每个 Turn 只注入一次。
    /// Returns true on this turn's first call and false afterwards; memory context is injected once per turn.
    /// </summary>
    public bool TryBeginMemoryInjection() =>
        Parent?.TryBeginMemoryInjection() ?? Interlocked.Exchange(ref _memoryInjected, 1) == 0;

    /// <summary>
    /// 为本作用域绑定决定来源、回答通道与请求登记服务；每个作用域只绑定一次。
    /// Binds the decision source, answer channel and request registry of this scope; each scope binds once.
    /// </summary>
    public void BindInteractions(
        IInteractionHandler? handler,
        IHumanInteractionChannel? channel,
        IInteractionRequestRegistry? requests
    )
    {
        if (Interlocked.Exchange(ref _interactionsBound, 1) != 0)
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "The execution scope already has an interaction channel."
            );
        InteractionHandler = handler;
        InteractionChannel = channel;
        InteractionRequests = requests;
    }

    /// <summary>
    /// 记录本作用域执行的结局；每个作用域只报告一次。
    /// Records the outcome of this scope's execution; each scope reports once.
    /// </summary>
    public void ReportOutcome(TurnOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (Outcome != null)
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "The execution scope already has an outcome.");
        Outcome = outcome;
    }

    /// <summary>
    /// 为 Agentflow 节点的一次执行派生子作用域：当前执行者与节点信息改为节点的值，其余数据与父作用域一致。
    /// Derives the scope of one Agentflow node execution: the executor and node change, all other data matches the parent.
    /// </summary>
    public ExecutionScope CreateNodeScope(AgentflowNodeExecution node, Guid? agentId, EngineKind? engineKind)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CreateChild(
            Context with
            {
                AgentId = agentId,
                EngineKind = engineKind,
                Node = node,
            },
            InteractionHandler,
            InteractionChannel,
            InteractionRequests
        );
    }

    /// <summary>
    /// 为后台 Agent 派生子作用域：当前执行者改为后台 Agent，且不能向用户发起交互。
    /// Derives the scope of a background Agent: the executor changes and no user interaction is available.
    /// </summary>
    public ExecutionScope CreateBackgroundAgentScope(Guid agentId, EngineKind engineKind) =>
        CreateChild(
            Context with
            {
                AgentId = agentId,
                EngineKind = engineKind,
            },
            handler: null,
            channel: null,
            requests: null
        );

    /// <summary>
    /// 建立执行数据与 UserInfoUtil 身份；释放时一起恢复。已有用户与执行用户不同时抛出 ExecutionOwnerMismatch。
    /// Establishes the execution data and the UserInfoUtil identity, restoring both on dispose; throws ExecutionOwnerMismatch when another user is active.
    /// </summary>
    public IDisposable Push()
    {
        if (
            UserInfoUtil.UserId is { } currentUserId
            && !string.Equals(currentUserId, Context.UserId, StringComparison.Ordinal)
        )
            throw new AgwException(ErrorCodes.ExecutionOwnerMismatch);
        var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Context.UserId)], "Execution"))
        );
        var slotScope = ExecutionContextSlot.Push(this);
        return new PushScope(slotScope, userScope);
    }

    /// <summary>
    /// 在每次枚举推进与释放时重新建立本作用域；异步迭代器内部的 AsyncLocal 修改在 yield 之后不会保留。
    /// Re-establishes this scope around every MoveNext and Dispose; AsyncLocal changes inside an iterator do not survive a yield.
    /// </summary>
    public IAsyncEnumerable<T> RunStreaming<T>(IAsyncEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ScopedEnumerable<T>(this, source);
    }

    private ExecutionScope CreateChild(
        AgentExecutionContext childContext,
        IInteractionHandler? handler,
        IHumanInteractionChannel? channel,
        IInteractionRequestRegistry? requests
    )
    {
        var child = new ExecutionScope(
            childContext,
            TaskId,
            Output,
            Interactions,
            Checkpoints,
            this,
            turn: null,
            writeGuard: null
        );
        child.BindInteractions(handler, channel, requests);
        return child;
    }

    private sealed class PushScope : IDisposable
    {
        private readonly IDisposable _slotScope;
        private readonly IDisposable _userScope;
        private int _disposed;

        public PushScope(IDisposable slotScope, IDisposable userScope)
        {
            _slotScope = slotScope;
            _userScope = userScope;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _slotScope.Dispose();
            _userScope.Dispose();
        }
    }

    private sealed class ScopedEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly ExecutionScope _scope;
        private readonly IAsyncEnumerable<T> _source;

        public ScopedEnumerable(ExecutionScope scope, IAsyncEnumerable<T> source)
        {
            _scope = scope;
            _source = source;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ScopedEnumerator<T>(_scope, _source, cancellationToken);
    }

    private sealed class ScopedEnumerator<T> : IAsyncEnumerator<T>
    {
        private readonly ExecutionScope _scope;
        private readonly IAsyncEnumerable<T> _source;
        private readonly CancellationToken _cancellationToken;
        private IAsyncEnumerator<T>? _inner;
        private int _disposed;

        public ScopedEnumerator(ExecutionScope scope, IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        {
            _scope = scope;
            _source = source;
            _cancellationToken = cancellationToken;
        }

        public T Current => _inner!.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            // 同步部分在作用域内开始；异步延续沿用开始时捕获的执行上下文。
            // The synchronous part starts inside the scope; asynchronous continuations keep the captured execution context.
            using var pushed = _scope.Push();
            _inner ??= _source.GetAsyncEnumerator(_cancellationToken);
            return _inner.MoveNextAsync();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _inner == null)
                return ValueTask.CompletedTask;
            using var pushed = _scope.Push();
            return _inner.DisposeAsync();
        }
    }
}
