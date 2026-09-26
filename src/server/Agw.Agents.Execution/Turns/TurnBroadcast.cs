using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Outbound;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// Turn 内一条已编号的消息；Sequence 即 turnSequence。PayloadJson 是数据库中未加序号的载荷，只在已提交的事件上存在。
/// One numbered message of a turn; Sequence is the turnSequence. PayloadJson is the unnumbered payload stored in the database and exists only on committed events.
/// </summary>
internal readonly record struct TurnBroadcastEntry(long Sequence, AgwMessage Message, string? PayloadJson = null);

/// <summary>
/// 执行实例内一个 Turn 的广播：保存本 Turn 的回放缓冲，按序号交给订阅者。进程内模式由它为消息分配 turnSequence，
/// 并把同一消息在 CoalescingWindow 内相邻的流式文本增量合并成一条；Durable 模式只发布数据库已经提交并编号的事件。
/// The broadcast of one turn inside the executing instance: keeps the turn's replay buffer and hands messages to subscribers in sequence order.
/// In-process mode assigns the turnSequence here and merges adjacent streaming text deltas of one message within CoalescingWindow; Durable mode only publishes events the database has committed and numbered.
/// </summary>
internal sealed class TurnBroadcast : IExecutionMessageSink
{
    /// <summary>
    /// 进程内合并流式文本增量的时间窗口，与客户端 50 ms 的批量渲染间隔一致。
    /// The window for merging in-process streaming text deltas, matching the client's 50 ms render batch interval.
    /// </summary>
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(50);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<TurnBroadcastEntry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private IExecutionMessageSink[] _sinks = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _changedObserved;
    private long _lastSequence;
    private AgwMessage? _pending;
    private ITimer? _flushTimer;
    private ExceptionDispatchInfo? _flushFailure;

    public TurnBroadcast(Guid turnId, string userId, TimeProvider timeProvider)
    {
        TurnId = turnId;
        UserId = userId;
        _timeProvider = timeProvider;
    }

    public Guid TurnId { get; }

    public string UserId { get; }

    public bool IsFinished { get; private set; }

    public long LastSequence
    {
        get
        {
            lock (_gate)
                return _lastSequence;
        }
    }

    /// <summary>
    /// 结束消息写入后触发，登记表据此开始计算保留时间。
    /// Raised once the finish message is written; the registry starts the retention period from it.
    /// </summary>
    public event Action<TurnBroadcast>? Finished;

    /// <summary>
    /// 进程内输出：分配下一个序号，写入回放缓冲，再依次交给同步订阅者。可合并的流式文本增量先保存在待发布位置，
    /// 同一消息的后续增量并入其中；其他消息或计时到期时先发布它，因此所有写入方的消息保持写入顺序。
    /// In-process output: assigns the next sequence, appends to the replay buffer, then hands the message to every inline subscriber. A mergeable streaming text delta waits in the pending slot
    /// and later deltas of the same message merge into it; any other message or the timer publishes it first, so messages from every writer keep their write order.
    /// </summary>
    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // 计时发布时订阅者抛出的异常交给下一次写入，与直接写入时的失败传递方式一致。
            // An exception a subscriber threw during a timed publication reaches the next write, as a direct write failure would.
            if (_flushFailure is { } failure)
            {
                _flushFailure = null;
                failure.Throw();
            }

            if (_pending != null)
            {
                if (StreamingMessageMerger.CanMerge(_pending, message))
                {
                    _pending = StreamingMessageMerger.Merge(_pending, message);
                    return;
                }
                await PublishPendingAsync().ConfigureAwait(false);
            }

            if (StreamingMessageMerger.CanMerge(message))
            {
                _pending = StreamingMessageMerger.Own(message);
                _flushTimer ??= _timeProvider.CreateTimer(
                    static state => ((TurnBroadcast)state!).OnFlushTimer(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan
                );
                _flushTimer.Change(CoalescingWindow, Timeout.InfiniteTimeSpan);
                return;
            }

            await PublishAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void OnFlushTimer() => _ = FlushPendingAsync();

    private async Task FlushPendingAsync()
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_pending != null)
                await PublishPendingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _flushFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ValueTask PublishPendingAsync()
    {
        var pending = _pending!;
        _pending = null;
        _flushTimer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return PublishAsync(pending);
    }

    private async ValueTask PublishAsync(AgwMessage message)
    {
        TurnBroadcastEntry entry;
        IExecutionMessageSink[] sinks;
        bool finished;
        lock (_gate)
        {
            entry = new TurnBroadcastEntry(_lastSequence + 1, Stamp(message, TurnId, _lastSequence + 1));
            finished = Append(entry);
            sinks = _sinks;
        }
        foreach (var sink in sinks)
            await sink.WriteAsync(entry.Message, CancellationToken.None).ConfigureAwait(false);
        if (finished)
        {
            _flushTimer?.Dispose();
            Finished?.Invoke(this);
        }
    }

    /// <summary>
    /// Durable 输出：发布数据库已提交的事件。已经发布过的序号被忽略；序号不连续时只保留连续的部分，读者从数据库补齐。
    /// Durable output: publishes events the database has committed. Sequences already published are ignored; with a gap only the contiguous part is kept and readers fill the rest from the database.
    /// </summary>
    public void PublishCommitted(IReadOnlyList<TurnBroadcastEntry> entries)
    {
        var finished = false;
        lock (_gate)
        {
            foreach (var entry in entries.OrderBy(item => item.Sequence))
            {
                if (entry.Sequence <= _lastSequence)
                    continue;
                if (_entries.Count > 0 && entry.Sequence != _lastSequence + 1)
                    _entries.Clear();
                // 回放缓冲只需要消息；数据库载荷已经交给事件记录与投影。
                // The replay buffer needs only the message; the stored payload already went to the event log and projection.
                finished |= Append(entry with { PayloadJson = null });
            }
        }
        if (finished)
            Finished?.Invoke(this);
    }

    /// <summary>
    /// 同步订阅：之后写入的每条消息在 WriteAsync 内按顺序交给 sink。
    /// Inline subscription: every later message reaches the sink in order inside WriteAsync.
    /// </summary>
    public void AddSink(IExecutionMessageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
            _sinks = [.. _sinks, sink];
    }

    /// <summary>
    /// 订阅者数组写时复制：发布消息时直接读取当前数组，不必每条消息复制一次。
    /// The subscriber array is copied on write, so publishing reads the current array without copying it per message.
    /// </summary>
    public void RemoveSink(IExecutionMessageSink sink)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_sinks, sink);
            if (index >= 0)
                _sinks = [.. _sinks.AsSpan(0, index), .. _sinks.AsSpan(index + 1)];
        }
    }

    /// <summary>
    /// 先回放缓冲中序号大于 afterSequence 的消息，再成为同步订阅者；回放期间的新消息排在回放之后。
    /// Replays buffered messages after afterSequence, then becomes an inline subscriber; messages written during the replay follow it.
    /// </summary>
    public async Task AttachAsync(IExecutionMessageSink sink, long afterSequence)
    {
        ArgumentNullException.ThrowIfNull(sink);
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            TurnBroadcastEntry[] replay;
            lock (_gate)
            {
                replay = EntriesAfter(afterSequence);
                _sinks = [.. _sinks, sink];
            }
            foreach (var entry in replay)
                await sink.WriteAsync(entry.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 读取缓冲中序号大于 afterSequence 的消息；缓冲的最早消息晚于 afterSequence + 1 时返回空，读者需要先从数据库补齐。
    /// Reads buffered messages after afterSequence; returns none when the earliest buffered message is later than afterSequence + 1, so the reader fills the gap from the database first.
    /// </summary>
    public IReadOnlyList<TurnBroadcastEntry> ReadAfter(long afterSequence, out Task changed)
    {
        lock (_gate)
        {
            _changedObserved = true;
            changed = _changed.Task;
            if (_entries.Count == 0 || _entries[0].Sequence > afterSequence + 1)
                return [];
            return EntriesAfter(afterSequence);
        }
    }

    /// <summary>
    /// 缓冲中的序号连续，按 afterSequence 直接算出起始下标后切片复制。调用方持有 _gate。
    /// Buffered sequences are contiguous, so the start index follows from afterSequence and the tail is copied as a slice. The caller holds _gate.
    /// </summary>
    private TurnBroadcastEntry[] EntriesAfter(long afterSequence)
    {
        if (_entries.Count == 0)
            return [];
        var start = Math.Max(0, afterSequence + 1 - _entries[0].Sequence);
        return start >= _entries.Count ? [] : CollectionsMarshal.AsSpan(_entries)[(int)start..].ToArray();
    }

    /// <summary>
    /// 给 Turn 内的消息加上 turnId 与 turnSequence。
    /// Adds the turnId and turnSequence to a message of the turn.
    /// </summary>
    public static AgwMessage Stamp(AgwMessage message, Guid turnId, long sequence)
    {
        var properties =
            message.AdditionalProperties == null
                ? []
                : new AdditionalPropertiesDictionary(message.AdditionalProperties);
        properties[TurnMessageFactory.TurnIdKey] = turnId.ToString("D");
        properties[TurnMessageFactory.TurnSequenceKey] = sequence;
        return message with { AdditionalProperties = properties };
    }

    /// <summary>
    /// 追加一条消息并唤醒等待者；返回这条消息是否第一次让 Turn 结束。
    /// Appends one message and wakes waiters; returns whether this message finished the turn for the first time.
    /// </summary>
    private bool Append(TurnBroadcastEntry entry)
    {
        _entries.Add(entry);
        _lastSequence = entry.Sequence;
        var finished = !IsFinished && AgwMessageClassifier.IsTurnFinished(entry.Message);
        IsFinished |= finished;
        // 只有读者取走过当前的 changed 任务时才需要唤醒并换一个新任务。
        // Only when a reader took the current changed task does it need waking and replacing.
        if (_changedObserved)
        {
            _changed.TrySetResult();
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _changedObserved = false;
        }
        return finished;
    }
}

/// <summary>
/// 本实例内按 turnId 登记的广播；Turn 结束后保留 TurnBroadcastRetentionSeconds，供后来的订阅回放。
/// The broadcasts of this instance registered by turnId; a finished turn stays for TurnBroadcastRetentionSeconds so later subscriptions can replay it.
/// </summary>
internal sealed class TurnBroadcastRegistry
{
    private readonly ConcurrentDictionary<Guid, TurnBroadcast> _broadcasts = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    public TurnBroadcastRegistry(TimeProvider timeProvider, IOptions<ExecutionRuntimeOptions> options)
    {
        _timeProvider = timeProvider;
        _retention = TimeSpan.FromSeconds(options.Value.TurnBroadcastRetentionSeconds);
    }

    public TurnBroadcast GetOrCreate(Guid turnId, string userId)
    {
        var broadcast = _broadcasts.GetOrAdd(
            turnId,
            id =>
            {
                var created = new TurnBroadcast(id, userId, _timeProvider);
                created.Finished += ScheduleRemoval;
                return created;
            }
        );
        return string.Equals(broadcast.UserId, userId, StringComparison.Ordinal)
            ? broadcast
            : throw new AgwException(ErrorCodes.ResourceNotFound);
    }

    /// <summary>
    /// 只返回属于该用户的广播；其他用户的 turnId 与不存在一样。
    /// Returns only a broadcast owned by the user; another user's turnId looks like a missing one.
    /// </summary>
    public TurnBroadcast? Find(Guid turnId, string userId) =>
        _broadcasts.TryGetValue(turnId, out var broadcast)
        && string.Equals(broadcast.UserId, userId, StringComparison.Ordinal)
            ? broadcast
            : null;

    private void ScheduleRemoval(TurnBroadcast broadcast) => _ = RemoveLaterAsync(broadcast);

    private async Task RemoveLaterAsync(TurnBroadcast broadcast)
    {
        await Task.Delay(_retention, _timeProvider).ConfigureAwait(false);
        _broadcasts.TryRemove(new KeyValuePair<Guid, TurnBroadcast>(broadcast.TurnId, broadcast));
    }
}
