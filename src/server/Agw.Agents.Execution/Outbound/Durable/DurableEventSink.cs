using System.Runtime.ExceptionServices;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Contracts.Coordination;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Outbound.Durable;

/// <summary>
/// 一个 Segment 的事件输出：按批经写入入口提交事件并分配 turnSequence，提交成功后才发布到本实例广播与 Redis 投影。
/// interaction-request 与结束消息在 Segment 结果事务中与状态一起提交，这里不写入。
/// The event output of one segment: commits events in batches through the write guard, assigning turnSequences, and only after a successful commit publishes them to this instance's broadcast and the Redis projection.
/// interaction-request and finish messages commit with the state in the segment result transaction and are not written here.
/// </summary>
internal sealed class DurableEventSink : IExecutionMessageSink, IAsyncDisposable
{
    private readonly IExecutionWriteGuard _writeGuard;
    private readonly DurableLease _lease;
    private readonly int _segmentIndex;
    private readonly TurnBroadcast _broadcast;
    private readonly DurableExecutionEventLog _eventLog;
    private readonly ExecutionEventStreamOptions _options;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _ownershipLost;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly List<PendingExecutionEvent> _pending = [];
    private readonly Task _timer;
    private DateTimeOffset? _flushAt;
    private volatile ExceptionDispatchInfo? _failure;
    private int _closed;

    public DurableEventSink(
        IExecutionWriteGuard writeGuard,
        DurableLease lease,
        int segmentIndex,
        TurnBroadcast broadcast,
        DurableExecutionEventLog eventLog,
        ExecutionEventStreamOptions options,
        TimeProvider timeProvider,
        CancellationToken ownershipLost
    )
    {
        _writeGuard = writeGuard;
        _lease = lease;
        _segmentIndex = segmentIndex;
        _broadcast = broadcast;
        _eventLog = eventLog;
        _options = options;
        _clock = timeProvider;
        _ownershipLost = ownershipLost;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost);
        _timer = _options.WriteIntervalMilliseconds > 0 ? RunTimerAsync() : Task.CompletedTask;
    }

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCommittedWithState(message))
            return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _failure?.Throw();
            _ownershipLost.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0)
                return;
            if (_pending.Count == 0)
            {
                _flushAt = _clock.GetUtcNow().AddMilliseconds(_options.WriteIntervalMilliseconds);
                if (_signal.CurrentCount == 0)
                    _signal.Release();
            }

            // 生产者可能在 WriteAsync 返回后继续修改内容；批次保存独立的序列化快照。
            // Producers may mutate content after WriteAsync returns; the batch keeps an independent serialized snapshot.
            _pending.Add(PendingExecutionEvent.Create(message));
            if (_options.WriteIntervalMilliseconds == 0 || _pending.Count >= _options.WriteBatchSize)
                await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 提交全部待写事件；Segment 结果提交前调用，保证事件序号排在结果事件之前。
    /// Commits every pending event; called before the segment result commits so event sequences precede the result events.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _failure?.Throw();
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ownershipLost);
        var batch = _pending.ToArray();
        // 失败的批次保持原事件 ID，重试时已提交的部分返回原序号。
        // A failed batch keeps its event IDs, so a retry returns the original sequences of any committed part.
        var committed = await _writeGuard
            .RunAsync(
                (services, token) =>
                    DurableExecutionEvents.AppendAsync(
                        services.GetRequiredService<IAgentsDbContext>(),
                        _lease.ExecutionId,
                        _lease.Epoch,
                        _segmentIndex,
                        batch,
                        token
                    ),
                linked.Token
            )
            .ConfigureAwait(false);
        _pending.Clear();
        _flushAt = null;
        _broadcast.PublishCommitted(committed);
        await _eventLog.PublishAsync(_lease.ExecutionId, committed, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunTimerAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
                while (true)
                {
                    await _gate.WaitAsync(token).ConfigureAwait(false);
                    TimeSpan delay;
                    try
                    {
                        if (_flushAt == null)
                            break;
                        delay = _flushAt.Value - _clock.GetUtcNow();
                        if (delay <= TimeSpan.Zero)
                        {
                            await FlushCoreAsync(token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    await Task.Delay(delay, _clock, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _failure = ExceptionDispatchInfo.Capture(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _timer.ConfigureAwait(false);
            if (!_ownershipLost.IsCancellationRequested)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _clock);
                await FlushAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.Clear();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// interaction-request 与结束消息在状态提交的同一事务中写入，客户端不会回答尚未保存的请求。
    /// interaction-request and finish messages are written in the transaction that commits the state, so clients never answer an unsaved request.
    /// </summary>
    private static bool IsCommittedWithState(AgwMessage message) =>
        AgwMessageClassifier.IsInteractionRequest(message) || AgwMessageClassifier.IsTurnFinished(message);
}
