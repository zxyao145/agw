using System.Runtime.ExceptionServices;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agw.Projects.Infrastructure;

public sealed partial class ConversationHistoryStore
{
    /// <summary>
    /// 一次执行的历史缓冲：登记投影来源，按写入模式、字节上限或计时提交；读取时合并尚未提交的快照。
    /// The history buffer of one execution: registers projection sources and commits by write mode, byte limit or timer; reads merge snapshots not yet committed.
    /// </summary>
    private sealed class HistoryBuffer : IConversationHistoryBuffer
    {
        private readonly Dictionary<Guid, long> _messageOrder = [];
        private readonly List<PendingSource> _sources = [];
        private long _nextMessageOrder;
        private readonly CancellationToken _ownershipLost;
        private readonly IExecutionWriteGuard? _writeGuard;
        private readonly ClaimsPrincipal _principal;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly Task _timer;
        private DateTimeOffset? _flushAt;
        private readonly Guid _metricsId = Guid.NewGuid();
        private DateTimeOffset? _pendingSince;
        private long _bufferedBytes;
        private long _committedSequence = -1;
        private int _closed;
        private ExceptionDispatchInfo? _invalidated;

        public HistoryBuffer(
            ConversationHistoryStore store,
            ConversationHistoryScope scope,
            CancellationToken ownershipLost,
            IExecutionWriteGuard? writeGuard
        )
        {
            Store = store;
            Scope = scope;
            Owner = UserInfoUtil.RequiredUserId;
            IsExecutionBound = scope.IsExecutionBound;
            _ownershipLost = ownershipLost;
            _writeGuard = writeGuard;
            _principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "HistoryPersistence")
            );
            _timer =
                store._options.Mode == ConversationHistoryWriteMode.Interval ? RunTimerAsync() : Task.CompletedTask;
        }

        public ConversationHistoryStore Store { get; }
        public ConversationHistoryScope Scope { get; }
        public string Owner { get; }
        public bool IsExecutionBound { get; private set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long CommittedSequence => Volatile.Read(ref _committedSequence);

        private long BufferedBytes
        {
            get => _bufferedBytes;
            set
            {
                _bufferedBytes = value;
                _pendingSince = value == 0 ? null : _pendingSince ?? Store._timeProvider.GetUtcNow();
                ConversationHistoryBufferMetrics.Update(
                    _metricsId,
                    value,
                    _pendingSince ?? default,
                    Store._timeProvider
                );
            }
        }

        public List<PendingHistoryRecord> Snapshot() =>
            _sources
                .SelectMany(pending =>
                    pending
                        .Source.CapturePending()
                        .Where(snapshot => _messageOrder.ContainsKey(snapshot.MessageId))
                        .Select(snapshot => Store.CreateSnapshotRecord(pending.Scope, snapshot))
                )
                .OrderBy(GetMessageOrder)
                .ToList();

        public void EnsureActive()
        {
            _invalidated?.Throw();
            _ownershipLost.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        public async Task ScheduleAsync(
            ConversationMessageWriteScope scope,
            IConversationMessageSource source,
            long changedBytes,
            CancellationToken cancellationToken
        )
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(source);
            if (
                scope.ProjectId != Scope.ProjectId
                || !string.Equals(
                    ContextIdUtil.NormalizeContextId(scope.ContextId),
                    Scope.ContextId,
                    StringComparison.Ordinal
                )
            )
                throw new AgwException(
                    ErrorCodes.ConversationSessionConflict,
                    "The message belongs to another conversation than this history buffer."
                );
            if (scope.Generation != Scope.Generation || Owner != UserInfoUtil.RequiredUserId)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureActive();
                StartTimer();
                foreach (var id in source.GetPendingMessageIds())
                    RegisterMessage(id);
                if (!_sources.Any(pending => ReferenceEquals(pending.Source, source)))
                    _sources.Add(new PendingSource(source, scope));
                BufferedBytes += changedBytes;
                await FlushIfFullAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<IAsyncDisposable> EnterBarrierAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureActive();
                await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
                return new BarrierLease(Gate);
            }
            catch
            {
                Gate.Release();
                throw;
            }
        }

        public async ValueTask CompleteAsync(Exception? executionFailure)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                await _timer.ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), Store._timeProvider);
                await FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Store._logger.LogError(
                    exception,
                    "Final history flush failed for project {ProjectId} context {ContextId}.",
                    Scope.ProjectId,
                    Scope.ContextId
                );
                if (executionFailure == null)
                    throw;
            }
            finally
            {
                _sources.Clear();
                _messageOrder.Clear();
                BufferedBytes = 0;
                _stop.Dispose();
                // A late SDK callback can still observe this closed buffer. Keep the gate available to reject it safely.
            }
        }

        private long GetMessageOrder(PendingHistoryRecord record) =>
            _messageOrder.GetValueOrDefault(record.Id, long.MaxValue);

        private void RegisterMessage(Guid id)
        {
            if (!_messageOrder.ContainsKey(id))
                _messageOrder.Add(id, _nextMessageOrder++);
        }

        private void StartTimer()
        {
            if (_sources.Count != 0)
                return;
            _flushAt = Store._timeProvider.GetUtcNow().AddSeconds(Store._options.FlushIntervalSeconds);
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }

        private Task FlushIfFullAsync(CancellationToken token) =>
            Store._options.Mode == ConversationHistoryWriteMode.Immediate
            || BufferedBytes >= Store._options.MaxBufferedBytes
                ? FlushCoreAsync(token)
                : Task.CompletedTask;

        private async Task FlushCoreAsync(CancellationToken token)
        {
            _invalidated?.Throw();
            if (_sources.Count == 0)
                return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), Store._timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, _ownershipLost);
            var captured = _sources.ToDictionary(
                pending => pending.Source,
                pending =>
                    pending
                        .Source.CapturePending()
                        .Where(snapshot => _messageOrder.ContainsKey(snapshot.MessageId))
                        .ToArray()
            );
            var records = _sources
                .SelectMany(pending =>
                    captured[pending.Source].Select(snapshot => Store.CreateSnapshotRecord(pending.Scope, snapshot))
                )
                .OrderBy(GetMessageOrder)
                .ToList();
            long? committed;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                using var userScope = UserInfoUtil.Push(_principal);
                committed = await Store
                    .AppendRecordsAsync(
                        Scope.ProjectId,
                        Scope.ContextId,
                        records,
                        Scope.Generation,
                        IsExecutionBound,
                        _writeGuard,
                        linked.Token
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (_ownershipLost.IsCancellationRequested
                    || exception is AgwException error
                        && (
                            error.Code == ErrorCodes.ConversationSessionConflict.Code
                            || error.Code == ErrorCodes.ResourceNotFound.Code
                            || error.Code == ErrorCodes.AuthenticationRequired.Code
                        )
                )
            {
                _invalidated = ExceptionDispatchInfo.Capture(exception);
                _sources.Clear();
                BufferedBytes = 0;
                _flushAt = null;
                throw;
            }
            IsExecutionBound = true;
            if (committed.HasValue)
                Volatile.Write(ref _committedSequence, committed.Value);
            foreach (var (source, snapshots) in captured)
                source.Acknowledge(snapshots);
            _sources.Clear();
            BufferedBytes = 0;
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
                        await Gate.WaitAsync(token).ConfigureAwait(false);
                        TimeSpan delay;
                        try
                        {
                            if (_flushAt == null)
                                break;
                            delay = _flushAt.Value - Store._timeProvider.GetUtcNow();
                            if (delay <= TimeSpan.Zero)
                            {
                                try
                                {
                                    await FlushCoreAsync(token).ConfigureAwait(false);
                                }
                                catch (Exception exception)
                                    when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                                {
                                    Store._logger.LogWarning(
                                        exception,
                                        "History batch flush failed for project {ProjectId} context {ContextId}; invalidated={Invalidated}.",
                                        Scope.ProjectId,
                                        Scope.ContextId,
                                        _invalidated != null
                                    );
                                    if (_invalidated == null)
                                        _flushAt = Store
                                            ._timeProvider.GetUtcNow()
                                            .AddSeconds(Store._options.FlushIntervalSeconds);
                                }
                                continue;
                            }
                        }
                        finally
                        {
                            Gate.Release();
                        }
                        await Task.Delay(delay, Store._timeProvider, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private sealed record PendingSource(IConversationMessageSource Source, ConversationMessageWriteScope Scope);

        private sealed class BarrierLease : IAsyncDisposable
        {
            private readonly SemaphoreSlim _gate;

            public BarrierLease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public ValueTask DisposeAsync()
            {
                _gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
