using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;

namespace Agw.Projects.Infrastructure;

public sealed partial class EfCoreChatHistoryProvider : IConversationMessageWriter, IConversationMessageInputs
{
    private readonly ConditionalWeakTable<IConversationMessageSource, SemaphoreSlim> _sourceWriteGates = new();

    public async ValueTask PersistInputsAsync(
        Microsoft.Agents.AI.AgentSession session,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        Guid producerId,
        CancellationToken cancellationToken
    )
    {
        var state = _state.GetOrInitializeState(session);
        var input = GetPendingRequest(session)
            .Concat(messages)
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .Where(message =>
                message.GetAgentRequestMessageSourceType() == Microsoft.Agents.AI.AgentRequestMessageSourceType.External
            )
            .Concat(ConversationHistoryPrelude.Take(session));
        var records = CreateRecords(input, state.HistoryScope, producerId, _timeProvider.GetUtcNow());
        var buffer = GetBuffer(state.ProjectId, state.ContextId, state.Generation);
        if (buffer != null)
            await buffer.AppendAsync(records, cancellationToken).ConfigureAwait(false);
        else
            await AppendRecordsAsync(
                    state.ProjectId,
                    state.ContextId,
                    records,
                    state.Generation,
                    state.IsExecutionBound,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ClearPendingRequest(session);
    }

    public async Task ScheduleAsync(
        ConversationMessageWriteScope scope,
        IConversationMessageSource source,
        long changedBytes,
        CancellationToken cancellationToken
    )
    {
        var buffer = GetBuffer(scope.ProjectId, scope.ContextId, scope.Generation);
        if (buffer == null)
        {
            var gate = _sourceWriteGates.GetValue(source, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Callers queued behind an in-flight write find the source already clean: the writer
                // that held the gate captured their changes too. Skip their round trip entirely.
                var snapshots = source.CapturePending();
                if (snapshots.Count == 0)
                    return;
                await UpsertAsync(scope, snapshots, cancellationToken).ConfigureAwait(false);
                source.Acknowledge(snapshots);
            }
            finally
            {
                gate.Release();
            }
            return;
        }
        await buffer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            buffer.EnsureActive();
            await buffer.EnqueueSourceAsync(scope, source, changedBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffer.Gate.Release();
        }
    }

    public Task UpsertAsync(
        ConversationMessageWriteScope scope,
        IReadOnlyList<ConversationMessageSnapshot> snapshots,
        CancellationToken cancellationToken
    ) =>
        AppendRecordsAsync(
            scope.ProjectId,
            ContextIdUtil.NormalizeContextId(scope.ContextId),
            snapshots.Select(snapshot => CreateSnapshotRecord(scope, snapshot)).ToArray(),
            scope.Generation,
            scope.IsExecutionBound,
            cancellationToken
        );

    private PendingHistoryRecord CreateSnapshotRecord(
        ConversationMessageWriteScope scope,
        ConversationMessageSnapshot snapshot
    )
    {
        if (scope.ProducerId == Guid.Empty || snapshot.MessageId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam);
        var message = JsonSerializer.Deserialize<Microsoft.Extensions.AI.ChatMessage>(
            snapshot.Payload,
            _jsonSerializerOptions
        );
        if (message == null || !Guid.TryParse(message.MessageId, out var messageId) || messageId != snapshot.MessageId)
            throw new AgwException(ErrorCodes.InvalidParam);
        var metadata = CreateMetadata(message, scope.HistoryScope) ?? [];
        foreach (var (key, value) in snapshot.Metadata)
            metadata[key] = value;
        metadata["producerScopeId"] = JsonSerializer.SerializeToElement(scope.ProducerId);
        metadata["generation"] = JsonSerializer.SerializeToElement(scope.Generation);
        if (scope.HistoryScope != null)
            metadata[HistoryScopeMetadataKey] = JsonSerializer.SerializeToElement(scope.HistoryScope);
        return new PendingHistoryRecord(
            snapshot.MessageId,
            scope.ProducerId,
            _timeProvider.GetUtcNow(),
            snapshot.Author,
            null,
            snapshot.Payload,
            metadata,
            IsStreamingSnapshot: true,
            Scope: scope
        );
    }
}
