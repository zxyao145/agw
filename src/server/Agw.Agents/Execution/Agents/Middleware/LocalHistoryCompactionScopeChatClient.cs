using System.Runtime.CompilerServices;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class LocalHistoryCompactionScopeChatClient : DelegatingChatClient
{
    public LocalHistoryCompactionScopeChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var originalContext = AIAgent.CurrentRunContext;
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            return await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var originalContext = AIAgent.CurrentRunContext;
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            await foreach (var update in base.GetStreamingResponseAsync(
                               messages,
                               options,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(
                               messages,
                               options,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    private static AgentRunContext? CreateCompactionContext(
        AgentRunContext? runContext,
        ChatOptions? options)
    {
        if (runContext?.Session == null || !string.IsNullOrWhiteSpace(options?.ConversationId))
        {
            return null;
        }

        var chatClientSession = runContext.Session.GetService<ChatClientAgentSession>();
        if (string.IsNullOrWhiteSpace(chatClientSession?.ConversationId))
        {
            return null;
        }

        // Per-service-call persistence assigns a local conversation sentinel after the first
        // model call. CompactionProvider treats every conversation id as remotely managed and
        // would otherwise skip the remaining calls in the function invocation loop.
        var localHistorySession = new LocalHistorySession(runContext.Session);
        return new AgentRunContext(
            runContext.Agent,
            localHistorySession,
            runContext.RequestMessages,
            runContext.RunOptions);
    }

    private sealed class LocalHistorySession : AgentSession
    {
        private readonly AgentSession _innerSession;

        public LocalHistorySession(AgentSession innerSession)
            : base(innerSession.StateBag)
        {
            _innerSession = innerSession;
        }

        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey == null && serviceType == typeof(ChatClientAgentSession))
            {
                return null;
            }

            return _innerSession.GetService(serviceType, serviceKey) ??
                base.GetService(serviceType, serviceKey);
        }
    }

    private abstract class RunContextAccessor : AIAgent
    {
        public static void SetCurrent(AgentRunContext? context)
        {
            CurrentRunContext = context;
        }
    }
}
