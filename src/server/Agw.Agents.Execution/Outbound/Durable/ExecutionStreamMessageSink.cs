using System.Runtime.ExceptionServices;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Outbound.Durable;

/// <summary>
/// A bounded serial batch writer for one segment attempt. Replay outages do not fail execution.
/// </summary>
internal sealed class ExecutionStreamMessageSink : IExecutionMessageSink, IAsyncDisposable
{
    private readonly IExecutionEventStream _stream;
    private readonly Guid _executionId;
    private readonly int _segmentIndex;
    private readonly ILogger _logger;
    private readonly ExecutionEventStreamOptions _options;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _invalidated;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly List<ExecutionStreamWrite> _pending = [];
    private readonly Task _timer;
    private DateTimeOffset? _flushAt;
    private volatile ExceptionDispatchInfo? _failure;
    private int _sequence = -1;
    private bool _disabled;
    private int _closed;

    public ExecutionStreamMessageSink(
        IExecutionEventStream stream,
        Guid executionId,
        int segmentIndex,
        ILogger logger,
        ExecutionEventStreamOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken invalidated = default
    )
    {
        _stream = stream;
        _executionId = executionId;
        _segmentIndex = segmentIndex;
        _logger = logger;
        _options = options ?? new ExecutionEventStreamOptions();
        _clock = timeProvider ?? TimeProvider.System;
        _invalidated = invalidated;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(invalidated);
        _timer = _options.WriteIntervalMilliseconds > 0 ? RunTimerAsync() : Task.CompletedTask;
    }

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDeferredControlMessage(message))
            return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _failure?.Throw();
            _invalidated.ThrowIfCancellationRequested();
            if (_disabled || Volatile.Read(ref _closed) != 0)
                return;
            if (_pending.Count == 0)
            {
                _flushAt = _clock.GetUtcNow().AddMilliseconds(_options.WriteIntervalMilliseconds);
                if (_signal.CurrentCount == 0)
                    _signal.Release();
            }

            // Producers may mutate content after WriteAsync returns. The batch owns an independent snapshot.
            _pending.Add(new ExecutionStreamWrite(++_sequence, message));
            if (_options.WriteIntervalMilliseconds == 0 || _pending.Count >= _options.WriteBatchSize)
                await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token,
            _invalidated
        );
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            await _stream.AppendBatchAsync(_executionId, _segmentIndex, _pending, linked.Token).ConfigureAwait(false);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionUnavailable.Code)
        {
            _disabled = true;
            _logger.LogWarning(
                exception,
                "Output replay is unavailable for distributed execution {ExecutionId} segment {SegmentIndex}; execution will continue without further stream output for this attempt.",
                _executionId,
                _segmentIndex
            );
        }

        _pending.Clear();
        _flushAt = null;
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
            if (!_invalidated.IsCancellationRequested)
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
    /// 判断控制消息是否必须延迟到 PostgreSQL 状态持久化后再发布。
    /// </summary>
    private static bool IsDeferredControlMessage(AgwMessage message)
    {
        if (
            message.AdditionalProperties == null
            || !message.AdditionalProperties.TryGetValue("type", out var value)
            || value is not string type
        )
        {
            return false;
        }

        return type is "interaction-request" or TurnMessageProtocol.FinishedType;
    }
}
