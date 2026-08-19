using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class LocalHistoryCompactionScopeChatClient : DelegatingChatClient
{
    private const string CompactionIndexVersion = "function-loop-context-v2";

    private readonly string? _compactionStateKey;
    private readonly string? _compactionVersionStateKey;
    private readonly ILogger<LocalHistoryCompactionScopeChatClient> _logger;

    public LocalHistoryCompactionScopeChatClient(
        IChatClient innerClient,
        string? compactionStateKey,
        ILogger<LocalHistoryCompactionScopeChatClient> logger
    )
        : base(innerClient)
    {
        _compactionStateKey = compactionStateKey;
        _compactionVersionStateKey = string.IsNullOrWhiteSpace(compactionStateKey)
            ? null
            : $"{compactionStateKey}.agw-index-version";
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var isolatedMessages = ChatMessageSourceIsolation.CloneMessages(messages);
        var originalContext = AIAgent.CurrentRunContext;
        EnsureCompatibleCompactionState(originalContext?.Session);
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            return await base.GetResponseAsync(isolatedMessages, options, cancellationToken).ConfigureAwait(false);
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            return await base.GetResponseAsync(isolatedMessages, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var isolatedMessages = ChatMessageSourceIsolation.CloneMessages(messages);
        var originalContext = AIAgent.CurrentRunContext;
        EnsureCompatibleCompactionState(originalContext?.Session);
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            await foreach (
                var update in base.GetStreamingResponseAsync(isolatedMessages, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return update;
            }

            yield break;
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            await foreach (
                var update in base.GetStreamingResponseAsync(isolatedMessages, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return update;
            }
        }
        finally
        {
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    private void EnsureCompatibleCompactionState(AgentSession? session)
    {
        if (
            session == null
            || string.IsNullOrWhiteSpace(_compactionStateKey)
            || string.IsNullOrWhiteSpace(_compactionVersionStateKey)
        )
        {
            return;
        }

        if (
            session.StateBag.TryGetValue<string>(_compactionVersionStateKey, out var version)
            && string.Equals(version, CompactionIndexVersion, StringComparison.Ordinal)
        )
        {
            return;
        }

        var resetExistingState = session.StateBag.TryRemoveValue(_compactionStateKey);
        session.StateBag.SetValue(_compactionVersionStateKey, CompactionIndexVersion);
        if (resetExistingState)
        {
            _logger.LogWarning(
                "Resetting legacy compaction state {CompactionStateKey} so it can be rebuilt from complete chat history.",
                _compactionStateKey
            );
        }
    }

    private static AgentRunContext? CreateCompactionContext(AgentRunContext? runContext, ChatOptions? options)
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
            runContext.RunOptions
        );
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

            return _innerSession.GetService(serviceType, serviceKey) ?? base.GetService(serviceType, serviceKey);
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
