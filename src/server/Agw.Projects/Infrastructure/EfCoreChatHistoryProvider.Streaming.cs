using System.Text.Json;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Infrastructure;

public sealed partial class EfCoreChatHistoryProvider
{
    public async ValueTask<IStreamingConversationHistory?> BeginStreamingResponseAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        var state = _state.GetOrInitializeState(session);
        var buffer = GetBuffer(state.ProjectId, state.ContextId, state.Generation);
        if (buffer == null)
            return null;
        var stream = new HistoryStream(this, buffer, state, agent.Name);
        var inputs = GetPendingRequest(session)
            .Concat(requestMessages)
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .Where(message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External)
            .Concat(ConversationHistoryPrelude.Take(session));
        var records = CreateRecords(inputs, state.HistoryScope, stream.TaskId, stream.Timestamp);
        if (records.Count > 0)
            await buffer.AppendAsync(records, cancellationToken).ConfigureAwait(false);
        _streams[session] = stream;
        ClearPendingRequest(session);
        return stream;
    }

    private sealed class HistoryStream : IStreamingConversationHistory
    {
        private readonly EfCoreChatHistoryProvider _provider;
        private readonly HistoryBuffer _buffer;
        private readonly State _state;
        private readonly string? _agentName;
        private readonly List<ChatResponseUpdate> _updates = [];
        private readonly List<Guid> _recordIds = [];
        private readonly SortedDictionary<int, PendingHistoryRecord> _pendingRecords = [];
        private ChatResponse _response = new([]);
        private int _dirtyFrom;
        private bool _completed;

        public Guid TaskId { get; } = Guid.CreateVersion7();
        public DateTimeOffset Timestamp { get; }

        public HistoryStream(EfCoreChatHistoryProvider provider, HistoryBuffer buffer, State state, string? agentName)
        {
            _provider = provider;
            _buffer = buffer;
            _state = state;
            _agentName = agentName;
            Timestamp = provider._timeProvider.GetUtcNow();
        }

        public async ValueTask AppendAsync(ChatResponseUpdate update, CancellationToken cancellationToken)
        {
            // Freeze each delta at entry. Aggregate only at a read/flush boundary, never rebuild a long response per token.
            var copy = update.Clone();
            copy.RawRepresentation = null;
            var payload = JsonSerializer.SerializeToUtf8Bytes(copy, _provider._jsonSerializerOptions);
            var snapshot = JsonSerializer.Deserialize<ChatResponseUpdate>(payload, _provider._jsonSerializerOptions)!;
            await _buffer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _buffer.EnsureActive();
                if (_completed)
                    throw new AgwException(ErrorCodes.ConversationSessionConflict);
                _updates.Add(snapshot);
                await _buffer.EnqueueStreamAsync(this, payload.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _buffer.Gate.Release();
            }
        }

        public async ValueTask CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(messages, _provider._jsonSerializerOptions);
            var snapshot = JsonSerializer.Deserialize<List<ChatMessage>>(payload, _provider._jsonSerializerOptions)!;
            await _buffer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _buffer.EnsureActive();
                _response = new ChatResponse(snapshot);
                _updates.Clear();
                _pendingRecords.Clear();
                _dirtyFrom = 0;
                _completed = true;
                await _buffer.EnqueueStreamAsync(this, payload.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _buffer.Gate.Release();
            }
        }

        // All calls hold the buffer gate, including model reads racing a timer or final SDK callback.
        public List<PendingHistoryRecord> Snapshot()
        {
            if (_updates.Count > 0)
            {
                // Later deltas can only extend the last message or start a new one. Keep the
                // unchanged prefix out of aggregation and serialization on subsequent flushes.
                var messages = _response.Messages;
                var tailIndex = Math.Max(0, messages.Count - 1);
                var tail = new ChatResponse(messages.Count == 0 ? [] : [messages[^1]])
                {
                    CreatedAt = _response.CreatedAt,
                    AdditionalProperties = _response.AdditionalProperties,
                    Usage = _response.Usage,
                };
                _response = tail.ToChatResponseUpdates().Concat(_updates).ToChatResponse();
                if (messages.Count > 0)
                    messages.RemoveAt(tailIndex);
                foreach (var message in _response.Messages)
                    messages.Add(message);
                _response.Messages = messages;
                _dirtyFrom = Math.Min(_dirtyFrom, tailIndex);
                _updates.Clear();
            }
            for (var index = _dirtyFrom; index < _response.Messages.Count; index++)
            {
                _pendingRecords.Remove(index);
                if (_recordIds.Count <= index)
                    _recordIds.Add(Guid.CreateVersion7());
                var source = _response.Messages[index];
                if (ConversationHistoryMetadata.IsPersistenceExcluded(source))
                    continue;
                var message = RemoveBlankTextualContent(AddResponseMetadata(source, _state.NodeName, _agentName));
                if (message == null)
                    continue;
                if (!_completed)
                {
                    message = message.Clone();
                    message.AdditionalProperties =
                        message.AdditionalProperties == null
                            ? []
                            : new AdditionalPropertiesDictionary(message.AdditionalProperties);
                    ConversationHistoryMetadata.ExcludeFromModelHistory(message);
                }
                _pendingRecords[index] = new PendingHistoryRecord(
                    _recordIds[index],
                    TaskId,
                    Timestamp,
                    message.AuthorName,
                    null,
                    JsonSerializer.Serialize(message, _provider._jsonSerializerOptions),
                    CreateMetadata(message, _state.HistoryScope),
                    IsStreamingSnapshot: true
                );
            }
            _dirtyFrom = _response.Messages.Count;
            return _pendingRecords.Values.ToList();
        }

        // A read or failed commit must retain these snapshots and their stable IDs for retry.
        public void MarkPersisted() => _pendingRecords.Clear();
    }
}
