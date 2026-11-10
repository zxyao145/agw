using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// 一条等待送达的 Steer：客户端生成的标识与用户输入。
/// One steering input waiting for delivery: the client-generated ID and the user input.
/// </summary>
internal sealed record SteerEntry(Guid SteerId, AgwUserInput Input);

internal enum SteerOfferResult
{
    /// <summary>已受理，等待送达。Accepted and waiting for delivery.</summary>
    Accepted,

    /// <summary>同一个 SteerId 已经受理过，不重复创建。The SteerId was accepted before and is not created again.</summary>
    Duplicate,

    /// <summary>Turn 已不再接收 Steer。The turn no longer accepts steering.</summary>
    Closed,

    /// <summary>等待送达的数量已达上限。The number of inputs waiting for delivery reached the limit.</summary>
    LimitExceeded,
}

/// <summary>
/// 执行一侧读取 Turn 待送达输入的入口：进程内由 SteerInbox 在内存中保存，Distributed 由数据库保存。
/// The execution side's access to a turn's inputs waiting for delivery: kept in memory by SteerInbox in process, and in the database for Distributed.
/// </summary>
internal interface ISteerInbox
{
    /// <summary>
    /// 取走全部等待中的输入，它们成为 Delivered。
    /// Takes all waiting inputs, which become Delivered.
    /// </summary>
    ValueTask<IReadOnlyList<SteerEntry>> TakePendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 模型不再请求工具、Turn 即将结束时取走等待中的输入；有输入就继续同一个 Turn。
    /// 进程内没有输入时在同一把锁内关闭；Distributed 由终态提交在锁定执行行的事务中交还之后到达的输入。
    /// Takes the waiting inputs when the model requests no more tools and the turn is about to end; inputs keep the same turn going.
    /// In process the inbox closes under the same lock when there are none; for Distributed the terminal commit hands back later inputs in the transaction that locks the execution row.
    /// </summary>
    ValueTask<IReadOnlyList<SteerEntry>> TakePendingOrCloseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 进程内每个活动 Turn 一个的待送达输入集合。Offer 把输入放入，引擎在下一次模型调用前取走；Turn 结束时 Close 交还剩余输入。
/// 每条输入恰好有一个终态：被取走（Delivered）或被 Close 交还（Returned）。取走与关闭在同一把锁内完成，不会出现两者兼有。
/// The in-process collection of inputs waiting for delivery, one per active turn. Offer adds an input and the engine takes it before the next model call; Close hands back the rest when the turn ends.
/// Every input has exactly one outcome: taken (Delivered) or handed back by Close (Returned). Taking and closing happen under one lock, so both cannot occur.
/// </summary>
internal sealed class SteerInbox : ISteerInbox
{
    private readonly Lock _sync = new();
    private readonly int _maxPending;
    private readonly List<SteerEntry> _pending = [];
    private readonly HashSet<Guid> _seen = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    public SteerInbox(int maxPending)
    {
        if (maxPending <= 0)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Execution:Steer:MaxPendingPerTurn must be greater than 0."
            );
        _maxPending = maxPending;
    }

    /// <summary>
    /// 放入一条输入；已受理的 SteerId 返回 Duplicate，Turn 已关闭返回 Closed，等待数量已满返回 LimitExceeded。
    /// Adds one input; a SteerId accepted before returns Duplicate, a closed turn returns Closed, and a full inbox returns LimitExceeded.
    /// </summary>
    public SteerOfferResult Offer(Guid steerId, AgwUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_sync)
        {
            if (_seen.Contains(steerId))
                return SteerOfferResult.Duplicate;
            if (_closed)
                return SteerOfferResult.Closed;
            if (_pending.Count >= _maxPending)
                return SteerOfferResult.LimitExceeded;
            _seen.Add(steerId);
            _pending.Add(new SteerEntry(steerId, input));
            SignalChanged();
            return SteerOfferResult.Accepted;
        }
    }

    public ValueTask<IReadOnlyList<SteerEntry>> TakePendingAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(TakePending());

    public ValueTask<IReadOnlyList<SteerEntry>> TakePendingOrCloseAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(TakePendingOrClose());

    /// <summary>
    /// 取走全部等待中的输入，它们成为 Delivered。
    /// Takes all waiting inputs, which become Delivered.
    /// </summary>
    public IReadOnlyList<SteerEntry> TakePending()
    {
        lock (_sync)
        {
            return DrainPending();
        }
    }

    /// <summary>
    /// 取走全部等待中的输入；没有等待的输入时关闭，之后的 Offer 返回 Closed。
    /// 模型不再请求工具、Turn 即将结束时调用：有输入就继续同一个 Turn，没有就在同一把锁内关闭，之后到达的输入不会被留下。
    /// Takes all waiting inputs; with none waiting it closes, and later Offers return Closed.
    /// Called when the model requests no more tools and the turn is about to end: inputs keep the same turn going, and with none the inbox closes under the same lock so nothing arrives after it.
    /// </summary>
    public IReadOnlyList<SteerEntry> TakePendingOrClose()
    {
        lock (_sync)
        {
            var taken = DrainPending();
            if (taken.Count == 0)
            {
                _closed = true;
                SignalChanged();
            }
            return taken;
        }
    }

    /// <summary>
    /// 把引擎没有接收的输入按原顺序放回队首，等待下一次送达机会。
    /// 外部引擎的送达任务在 Turn 关闭之前就已停止，放回时集合一定仍然打开，否则说明调用顺序有误。
    /// Puts inputs the engine did not accept back at the front in their original order, to wait for the next chance to deliver.
    /// The delivery task of an external engine stops before the turn closes, so the inbox must still be open; otherwise the call order is wrong.
    /// </summary>
    public void Requeue(IReadOnlyList<SteerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;
        lock (_sync)
        {
            if (_closed)
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    "Steering inputs cannot be requeued after the turn closed."
                );
            _pending.InsertRange(0, entries);
            SignalChanged();
        }
    }

    /// <summary>
    /// 等到有输入等待送达时返回 true，集合关闭时返回 false。
    /// Returns true once an input is waiting for delivery, and false once the inbox closes.
    /// </summary>
    public async Task<bool> WaitForPendingAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_pending.Count > 0)
                    return true;
                if (_closed)
                    return false;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 关闭并交还仍在等待的输入，它们成为 Returned。
    /// Closes and hands back the inputs still waiting, which become Returned.
    /// </summary>
    public IReadOnlyList<SteerEntry> Close()
    {
        lock (_sync)
        {
            _closed = true;
            SignalChanged();
            return DrainPending();
        }
    }

    private List<SteerEntry> DrainPending()
    {
        var taken = _pending.ToList();
        _pending.Clear();
        return taken;
    }

    /// <summary>
    /// 唤醒等待者并换一个新的信号；只在持有锁时调用。
    /// Wakes the waiters and replaces the signal; called only while holding the lock.
    /// </summary>
    private void SignalChanged()
    {
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }
}
