using System.Collections.Concurrent;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Outbound;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// Turn 内一条已编号的消息；Sequence 即 turnSequence。
/// One numbered message of a turn; Sequence is the turnSequence.
/// </summary>
internal sealed record TurnBroadcastEntry(long Sequence, AgwMessage Message);

/// <summary>
/// 执行实例内一个 Turn 的广播：保存本 Turn 的回放缓冲，按序号交给订阅者。进程内模式由它为消息分配 turnSequence；
/// Durable 模式只发布数据库已经提交并编号的事件。
/// The broadcast of one turn inside the executing instance: keeps the turn's replay buffer and hands messages to subscribers in sequence order.
/// In-process mode assigns the turnSequence here; Durable mode only publishes events the database has committed and numbered.
/// </summary>
internal sealed class TurnBroadcast : IExecutionMessageSink
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<TurnBroadcastEntry> _entries = [];
    private readonly List<IExecutionMessageSink> _sinks = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastSequence;

    public TurnBroadcast(Guid turnId, string userId)
    {
        TurnId = turnId;
        UserId = userId;
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
    /// 进程内输出：分配下一个序号，写入回放缓冲，再依次交给同步订阅者。
    /// In-process output: assigns the next sequence, appends to the replay buffer, then hands the message to every inline subscriber.
    /// </summary>
    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            TurnBroadcastEntry entry;
            IExecutionMessageSink[] sinks;
            bool finished;
            lock (_gate)
            {
                entry = new TurnBroadcastEntry(_lastSequence + 1, Stamp(message, TurnId, _lastSequence + 1));
                finished = Append(entry);
                sinks = _sinks.ToArray();
            }
            foreach (var sink in sinks)
                await sink.WriteAsync(entry.Message, CancellationToken.None).ConfigureAwait(false);
            if (finished)
                Finished?.Invoke(this);
        }
        finally
        {
            _writeGate.Release();
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
                finished |= Append(entry);
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
            _sinks.Add(sink);
    }

    public void RemoveSink(IExecutionMessageSink sink)
    {
        lock (_gate)
            _sinks.Remove(sink);
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
                replay = _entries.Where(entry => entry.Sequence > afterSequence).ToArray();
                _sinks.Add(sink);
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
            changed = _changed.Task;
            if (_entries.Count == 0 || _entries[0].Sequence > afterSequence + 1)
                return [];
            return _entries.Where(entry => entry.Sequence > afterSequence).ToArray();
        }
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
        _changed.TrySetResult();
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var created = new TurnBroadcast(id, userId);
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
