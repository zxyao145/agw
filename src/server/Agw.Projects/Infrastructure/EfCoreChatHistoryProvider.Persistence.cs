using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Projects.Infrastructure;

public sealed partial class EfCoreChatHistoryProvider
{
    public IConversationHistoryPersistenceScope BeginScope(
        Guid projectId,
        string contextId,
        int generation,
        CancellationToken ownershipLost = default,
        bool allowCreateConversation = false
    )
    {
        contextId = ContextIdUtil.NormalizeContextId(contextId);
        return GetBuffer(projectId, contextId, generation)
            ?? new HistoryBuffer(this, projectId, contextId, generation, ownershipLost, allowCreateConversation);
    }

    private HistoryBuffer? GetBuffer(Guid projectId, string contextId, int generation)
    {
        if (
            ConversationHistoryPersistenceContext.Current is not HistoryBuffer buffer
            || !ReferenceEquals(buffer.Provider, this)
            || buffer.ProjectId != projectId
            || !string.Equals(buffer.ContextId, contextId, StringComparison.Ordinal)
        )
            return null;
        if (buffer.Generation != generation || buffer.Owner != UserInfoUtil.RequiredUserId)
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        return buffer;
    }

    private async Task<List<ProjectConversationChatHistory>> ReadHistoryRecordsAsync(
        State state,
        CancellationToken token
    )
    {
        var buffer = GetBuffer(state.ProjectId, state.ContextId, state.Generation);
        if (buffer != null)
            await buffer.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            buffer?.EnsureActive();
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IProjectsDbContext>();
            var conversation = await db
                .ProjectConversations.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == state.ProjectId && item.ContextId == state.ContextId,
                    token
                )
                .ConfigureAwait(false);
            if (buffer != null && conversation != null && conversation.Generation != buffer.Generation)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            if (buffer != null && conversation == null && buffer.IsExecutionBound)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            var records =
                conversation == null
                    ? []
                    : await db
                        .ProjectConversationChatHistories.AsNoTracking()
                        .Where(record => record.ConversationId == conversation.Id && record.ConversationPayload != null)
                        .ToListAsync(token)
                        .ConfigureAwait(false);
            if (buffer != null)
            {
                var known = records.ToDictionary(record => record.Id);
                var sequence = records.Select(record => record.ConversationSequence ?? -1).DefaultIfEmpty(-1).Max();
                foreach (var record in buffer.Snapshot())
                {
                    if (known.TryGetValue(record.Id, out var existing))
                    {
                        if (record.IsStreamingSnapshot)
                        {
                            existing.ConversationPayload = record.Payload;
                            existing.Metadata = record.Metadata;
                        }
                    }
                    else
                        records.Add(ToEntity(record, conversation?.Id ?? Guid.Empty, ++sequence));
                }
            }
            return records;
        }
        finally
        {
            buffer?.Gate.Release();
        }
    }

    private sealed record PendingHistoryRecord(
        Guid Id,
        Guid TaskId,
        DateTimeOffset Timestamp,
        string? AgentName,
        string? UserText,
        string Payload,
        Dictionary<string, JsonElement>? Metadata,
        bool IsStreamingSnapshot = false,
        HistoryStream? Stream = null,
        IConversationMessageSource? Source = null,
        ConversationMessageWriteScope? Scope = null
    );

    private static ProjectConversationChatHistory ToEntity(
        PendingHistoryRecord record,
        Guid conversationId,
        long sequence
    ) =>
        new()
        {
            Id = record.Id,
            ConversationId = conversationId,
            TaskId = record.TaskId,
            Status = TaskExecutionStatus.Succeeded,
            AgentName = record.AgentName,
            ConversationSequence = sequence,
            ConversationPayload = record.Payload,
            Metadata = record.Metadata,
            CreateTime = record.Timestamp,
            UpdateTime = record.Timestamp,
        };

    private sealed class HistoryBuffer : IConversationHistoryPersistenceScope
    {
        private readonly Dictionary<Guid, long> _messageOrder = [];
        private long _nextMessageOrder;
        private readonly CancellationToken _ownershipLost;
        private readonly ClaimsPrincipal _principal;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly Task _timer;
        private DateTimeOffset? _flushAt;
        private readonly Guid _metricsId = Guid.NewGuid();
        private DateTimeOffset? _pendingSince;
        private long _bufferedBytes;
        private long BufferedBytes
        {
            get => _bufferedBytes;
            set
            {
                _bufferedBytes = value;
                _pendingSince = value == 0 ? null : _pendingSince ?? Provider._timeProvider.GetUtcNow();
                ConversationHistoryBufferMetrics.Update(
                    _metricsId,
                    value,
                    _pendingSince ?? default,
                    Provider._timeProvider
                );
            }
        }
        private int _closed;
        private ExceptionDispatchInfo? _invalidated;

        public EfCoreChatHistoryProvider Provider { get; }
        public Guid ProjectId { get; }
        public string ContextId { get; }
        public int Generation { get; }
        public string Owner { get; }
        public bool IsExecutionBound { get; private set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<PendingHistoryRecord> Records { get; } = [];

        public List<PendingHistoryRecord> Snapshot() =>
            Records
                .SelectMany(record =>
                    record.Source != null
                        ? record
                            .Source.CapturePending()
                            .Where(snapshot => _messageOrder.ContainsKey(snapshot.MessageId))
                            .Select(snapshot => Provider.CreateSnapshotRecord(record.Scope!, snapshot))
                        : record.Stream?.Snapshot() ?? [record]
                )
                .OrderBy(GetMessageOrder)
                .ToList();

        private long GetMessageOrder(PendingHistoryRecord record) =>
            _messageOrder.GetValueOrDefault(record.Id, _messageOrder.GetValueOrDefault(record.TaskId, long.MaxValue));

        private void RegisterMessage(Guid id)
        {
            if (!_messageOrder.ContainsKey(id))
                _messageOrder.Add(id, _nextMessageOrder++);
        }

        public HistoryBuffer(
            EfCoreChatHistoryProvider provider,
            Guid projectId,
            string contextId,
            int generation,
            CancellationToken ownershipLost,
            bool allowCreateConversation
        )
        {
            Provider = provider;
            ProjectId = projectId;
            ContextId = contextId;
            Generation = generation;
            Owner = UserInfoUtil.RequiredUserId;
            IsExecutionBound = !allowCreateConversation;
            _ownershipLost = ownershipLost;
            _principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "HistoryPersistence")
            );
            _timer =
                provider._options.Mode == ConversationHistoryWriteMode.Interval ? RunTimerAsync() : Task.CompletedTask;
        }

        public void EnsureActive()
        {
            _invalidated?.Throw();
            _ownershipLost.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        public async Task AppendAsync(IReadOnlyList<PendingHistoryRecord> records, CancellationToken token)
        {
            if (records.Count == 0)
                return;
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureActive();
                StartTimer();
                foreach (var record in records)
                    RegisterMessage(record.Id);
                Records.AddRange(records);
                BufferedBytes += records.Sum(record => (long)Encoding.UTF8.GetByteCount(record.Payload));
                if (records.Any(record => record.Metadata != null))
                {
                    // Count metadata in a reusable pooled buffer without retaining its JSON bytes.
                    using var scratch = new DiscardingBufferWriter();
                    using var writer = new Utf8JsonWriter(scratch);
                    foreach (var record in records)
                    {
                        if (record.Metadata == null)
                            continue;
                        JsonSerializer.Serialize(writer, record.Metadata);
                        BufferedBytes += writer.BytesCommitted + writer.BytesPending;
                        writer.Reset(scratch);
                    }
                }
                await FlushIfFullAsync(token).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private sealed class DiscardingBufferWriter : IBufferWriter<byte>, IDisposable
        {
            private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);

            // Utf8JsonWriter tracks the total byte count. Already-written bytes can be discarded.
            public void Advance(int count) { }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                if (sizeHint > _buffer.Length)
                {
                    var replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
                    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                    _buffer = replacement;
                }
                return _buffer;
            }

            public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

            public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        }

        public void StartTimer()
        {
            if (Records.Count != 0)
                return;
            _flushAt = Provider._timeProvider.GetUtcNow().AddSeconds(Provider._options.FlushIntervalSeconds);
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }

        public Task EnqueueStreamAsync(HistoryStream stream, long bytes, CancellationToken token)
        {
            // Caller owns Gate; a stream occupies one queue position regardless of its token count.
            RegisterMessage(stream.TaskId);
            StartTimer();
            if (!Records.Any(record => ReferenceEquals(record.Stream, stream)))
                Records.Add(
                    new PendingHistoryRecord(
                        stream.TaskId,
                        stream.TaskId,
                        stream.Timestamp,
                        null,
                        null,
                        "",
                        null,
                        Stream: stream
                    )
                );
            BufferedBytes += bytes;
            return FlushIfFullAsync(token);
        }

        public Task EnqueueSourceAsync(
            ConversationMessageWriteScope scope,
            IConversationMessageSource source,
            long bytes,
            CancellationToken token
        )
        {
            StartTimer();
            foreach (var id in source.GetPendingMessageIds())
                RegisterMessage(id);
            if (!Records.Any(record => ReferenceEquals(record.Source, source)))
                Records.Add(
                    new PendingHistoryRecord(
                        scope.ProducerId,
                        scope.ProducerId,
                        Provider._timeProvider.GetUtcNow(),
                        null,
                        null,
                        "",
                        null,
                        Source: source,
                        Scope: scope
                    )
                );
            BufferedBytes += bytes;
            return FlushIfFullAsync(token);
        }

        private Task FlushIfFullAsync(CancellationToken token) =>
            Provider._options.Mode == ConversationHistoryWriteMode.Immediate
            || BufferedBytes >= Provider._options.MaxBufferedBytes
                ? FlushCoreAsync(token)
                : Task.CompletedTask;

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

        private async Task FlushCoreAsync(CancellationToken token)
        {
            _invalidated?.Throw();
            if (Records.Count == 0)
                return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), Provider._timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, _ownershipLost);
            var captured = Records
                .Where(record => record.Source != null)
                .ToDictionary(
                    record => record.Source!,
                    record =>
                        record
                            .Source!.CapturePending()
                            .Where(snapshot => _messageOrder.ContainsKey(snapshot.MessageId))
                            .ToArray()
                );
            var snapshots = Records
                .SelectMany(record =>
                    record.Source != null
                        ? captured[record.Source]
                            .Select(snapshot => Provider.CreateSnapshotRecord(record.Scope!, snapshot))
                        : record.Stream?.Snapshot() ?? [record]
                )
                .OrderBy(GetMessageOrder)
                .ToList();
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                using var userScope = UserInfoUtil.Push(_principal);
                await Provider
                    .AppendRecordsAsync(ProjectId, ContextId, snapshots, Generation, IsExecutionBound, linked.Token)
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
                Records.Clear();
                BufferedBytes = 0;
                _flushAt = null;
                throw;
            }
            IsExecutionBound = true;
            foreach (var (source, messages) in captured)
                source.Acknowledge(messages);
            foreach (var record in Records)
                record.Stream?.MarkPersisted();
            Records.Clear();
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
                            delay = _flushAt.Value - Provider._timeProvider.GetUtcNow();
                            if (delay <= TimeSpan.Zero)
                            {
                                try
                                {
                                    await FlushCoreAsync(token).ConfigureAwait(false);
                                }
                                catch (Exception exception)
                                    when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                                {
                                    Provider._logger.LogWarning(
                                        exception,
                                        "History batch flush failed for project {ProjectId} context {ContextId}; invalidated={Invalidated}.",
                                        ProjectId,
                                        ContextId,
                                        _invalidated != null
                                    );
                                    if (_invalidated == null)
                                        _flushAt = Provider
                                            ._timeProvider.GetUtcNow()
                                            .AddSeconds(Provider._options.FlushIntervalSeconds);
                                }
                                continue;
                            }
                        }
                        finally
                        {
                            Gate.Release();
                        }
                        await Task.Delay(delay, Provider._timeProvider, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async ValueTask CompleteAsync(Exception? executionFailure)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                await _timer.ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), Provider._timeProvider);
                await FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Provider._logger.LogError(
                    exception,
                    "Final history flush failed for project {ProjectId} context {ContextId}.",
                    ProjectId,
                    ContextId
                );
                if (executionFailure == null)
                    throw;
            }
            finally
            {
                Records.Clear();
                _messageOrder.Clear();
                BufferedBytes = 0;
                _stop.Dispose();
                // A late SDK callback can still observe this closed scope. Keep the gate available to reject it safely.
            }
        }
    }
}
