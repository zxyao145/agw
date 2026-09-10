using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Infrastructure;

#pragma warning disable MAAI001 // MAF's existing history callback context is experimental.
public sealed partial class EfCoreChatHistoryProvider
{
    private readonly ConcurrentDictionary<AgentSession, IReadOnlyList<ChatMessage>> _pendingRequests = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly ConcurrentDictionary<AgentSession, IStreamingConversationHistory> _streams = new(
        ReferenceEqualityComparer.Instance
    );

    public void StageRequest(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            ClearPendingRequest(session);
            return;
        }
        // Keep the original input immutable and outside the serialized SDK session state.
        _pendingRequests[session] = JsonSerializer.Deserialize<List<ChatMessage>>(
            JsonSerializer.SerializeToUtf8Bytes(messages, _jsonSerializerOptions),
            _jsonSerializerOptions
        )!;
    }

    public async ValueTask PersistPendingAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        _streams.TryRemove(session, out _);
        var pending = GetPendingRequest(session);
        if (pending.Count == 0)
            return;
        try
        {
            await base.InvokedCoreAsync(new InvokedContext(agent, session, pending, []), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ClearPendingRequest(session);
        }
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (context.Session != null && _streams.TryRemove(context.Session, out var stream))
        {
            // Streaming inputs have already been queued after the model's history read.
            if (context.InvokeException == null && context.ResponseMessages?.Any() == true)
                await stream.CompleteAsync(context.ResponseMessages.ToList(), cancellationToken).ConfigureAwait(false);
            return;
        }

        var pending = GetPendingRequest(context.Session);
        var requests = pending
            .Concat(
                context.RequestMessages.Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            )
            .ToList();
        if (context.InvokeException != null)
        {
            if (pending.Count > 0)
            {
                await base.InvokedCoreAsync(
                        new InvokedContext(context.Agent, context.Session, requests, []),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                ClearPendingRequest(context.Session);
            }
            await base.InvokedCoreAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await base.InvokedCoreAsync(
                new InvokedContext(context.Agent, context.Session, requests, context.ResponseMessages ?? []),
                cancellationToken
            )
            .ConfigureAwait(false);
        ClearPendingRequest(context.Session);
    }

    private IReadOnlyList<ChatMessage> GetPendingRequest(AgentSession? session) =>
        session != null && _pendingRequests.TryGetValue(session, out var request) ? request : [];

    private void ClearPendingRequest(AgentSession? session)
    {
        if (session != null)
            _pendingRequests.TryRemove(session, out _);
    }
}
#pragma warning restore MAAI001
