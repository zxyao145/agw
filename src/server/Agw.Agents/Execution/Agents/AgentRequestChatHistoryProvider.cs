using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Merges one staged original request into the first history write and removes transient request copies.
/// </summary>
internal sealed class AgentRequestChatHistoryProvider : ChatHistoryProvider
{
    private readonly ChatHistoryProvider _innerProvider;
    private readonly ConcurrentDictionary<AgentSession, IReadOnlyList<ChatMessage>> _pendingRequests = new(
        ReferenceEqualityComparer.Instance
    );

    public AgentRequestChatHistoryProvider(ChatHistoryProvider innerProvider)
    {
        ArgumentNullException.ThrowIfNull(innerProvider);
        _innerProvider = innerProvider;
    }

    public override IReadOnlyList<string> StateKeys => _innerProvider.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => _innerProvider.InvokingAsync(context, cancellationToken);

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default
    )
    {
        var pendingRequest = GetPendingRequest(context.Session);
        var retainedRequestMessages = context
            .RequestMessages.Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .ToList();
        var requestMessages = pendingRequest.Concat(retainedRequestMessages).ToList();

        if (context.InvokeException != null)
        {
            if (pendingRequest.Count > 0)
            {
#pragma warning disable MAAI001
                var persistContext = new InvokedContext(context.Agent, context.Session, requestMessages, []);
#pragma warning restore MAAI001
                await _innerProvider.InvokedAsync(persistContext, cancellationToken).ConfigureAwait(false);
                ClearPendingRequest(context.Session);
            }

#pragma warning disable MAAI001
            var failedContext =
                pendingRequest.Count == 0
                    ? context
                    : new InvokedContext(
                        context.Agent,
                        context.Session,
                        retainedRequestMessages,
                        context.InvokeException
                    );
#pragma warning restore MAAI001
            await _innerProvider.InvokedAsync(failedContext, cancellationToken).ConfigureAwait(false);
            return;
        }

#pragma warning disable MAAI001
        var delegatedContext = new InvokedContext(
            context.Agent,
            context.Session,
            requestMessages,
            context.ResponseMessages ?? []
        );
#pragma warning restore MAAI001
        await _innerProvider.InvokedAsync(delegatedContext, cancellationToken).ConfigureAwait(false);
        ClearPendingRequest(context.Session);
    }

    internal void StageRequest(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            ClearPendingRequest(session);
            return;
        }

        _pendingRequests[session] = messages.Select(CloneMessage).ToList();
    }

    public async ValueTask PersistPendingAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        var pendingRequest = GetPendingRequest(session);
        if (pendingRequest.Count == 0)
        {
            return;
        }

#pragma warning disable MAAI001
        await _innerProvider
            .InvokedAsync(new InvokedContext(agent, session, pendingRequest, []), cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore MAAI001
        ClearPendingRequest(session);
    }

    private IReadOnlyList<ChatMessage> GetPendingRequest(AgentSession? session) =>
        session != null && _pendingRequests.TryGetValue(session, out var request) ? request : [];

    private void ClearPendingRequest(AgentSession? session)
    {
        if (session != null)
        {
            _pendingRequests.TryRemove(session, out _);
        }
    }

    private static ChatMessage CloneMessage(ChatMessage message)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.ToList();
        if (message.AdditionalProperties != null)
        {
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        }

        return clone;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _innerProvider.GetService(serviceType, serviceKey);
}
