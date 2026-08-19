using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Isolates function-loop messages from mutating middleware and removes unchanged context
/// messages when tool approval re-enters the agent during the same outer run.
/// </summary>
internal sealed class FunctionLoopMessageIsolationChatClient : DelegatingChatClient
{
    private readonly FunctionLoopContextTracker _contextTracker;

    public FunctionLoopMessageIsolationChatClient(IChatClient innerClient, FunctionLoopContextTracker contextTracker)
        : base(innerClient)
    {
        _contextTracker = contextTracker;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        base.GetResponseAsync(
            _contextTracker.FilterRepeatedContextMessages(ChatMessageSourceIsolation.CloneMessages(messages)),
            options,
            cancellationToken
        );

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) =>
        base.GetStreamingResponseAsync(
            _contextTracker.FilterRepeatedContextMessages(ChatMessageSourceIsolation.CloneMessages(messages)),
            options,
            cancellationToken
        );
}

/// <summary>
/// Keeps duplicate detection scoped to one caller-visible agent run. Auto-approved tool calls
/// can re-enter the inner agent several times during that run, but a later user turn starts fresh.
/// </summary>
internal sealed class FunctionLoopContextScopeAgent : DelegatingAIAgent
{
    private readonly FunctionLoopContextTracker _contextTracker;

    public FunctionLoopContextScopeAgent(AIAgent innerAgent, FunctionLoopContextTracker contextTracker)
        : base(innerAgent)
    {
        _contextTracker = contextTracker;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _contextTracker.BeginRun(session);
        return await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _contextTracker.BeginRun(session);
        await foreach (
            var update in InnerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }
}

internal sealed class FunctionLoopContextTracker
{
    private readonly ConcurrentDictionary<AgentSession, RunState> _runs = new(ReferenceEqualityComparer.Instance);

    public IDisposable BeginRun(AgentSession session)
    {
        var run = new RunState();
        _runs[session] = run;
        return new RunScope(this, session, run);
    }

    public List<ChatMessage> FilterRepeatedContextMessages(List<ChatMessage> messages)
    {
        var session = AIAgent.CurrentRunContext?.Session;
        if (session == null || !_runs.TryGetValue(session, out var run))
        {
            return messages;
        }

        var filtered = new List<ChatMessage>(messages.Count);
        var sourceOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (
                message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider
                || !TryCreateSignature(message, out var signature)
            )
            {
                filtered.Add(message);
                continue;
            }

            var sourceId = message.GetAgentRequestMessageSourceId() ?? string.Empty;
            sourceOrdinals.TryGetValue(sourceId, out var ordinal);
            sourceOrdinals[sourceId] = ordinal + 1;
            var key = new ContextMessageKey(sourceId, ordinal);
            if (run.ContextMessages.TryGetValue(key, out var previous) && previous == signature)
            {
                continue;
            }

            run.ContextMessages[key] = signature;
            filtered.Add(message);
        }

        return filtered;
    }

    private static bool TryCreateSignature(ChatMessage message, out ContextMessageSignature signature)
    {
        var content = new StringBuilder();
        foreach (var item in message.Contents)
        {
            switch (item)
            {
                case TextContent text:
                    AppendText(content, nameof(TextContent), text.Text);
                    break;
                case TextReasoningContent reasoning:
                    AppendText(content, nameof(TextReasoningContent), reasoning.Text);
                    break;
                default:
                    signature = default;
                    return false;
            }
        }

        signature = new ContextMessageSignature(message.Role.Value, message.AuthorName, content.ToString());
        return true;
    }

    private static void AppendText(StringBuilder builder, string kind, string text)
    {
        builder.Append(kind);
        builder.Append(':');
        builder.Append(text.Length);
        builder.Append(':');
        builder.Append(text);
        builder.Append(';');
    }

    private sealed class RunState
    {
        public Dictionary<ContextMessageKey, ContextMessageSignature> ContextMessages { get; } = [];
    }

    private sealed class RunScope : IDisposable
    {
        private readonly FunctionLoopContextTracker _owner;
        private readonly AgentSession _session;
        private readonly RunState _run;
        private int _disposed;

        public RunScope(FunctionLoopContextTracker owner, AgentSession session, RunState run)
        {
            _owner = owner;
            _session = session;
            _run = run;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner._runs.TryRemove(new KeyValuePair<AgentSession, RunState>(_session, _run));
            }
        }
    }

    private readonly record struct ContextMessageKey(string SourceId, int Ordinal);

    private readonly record struct ContextMessageSignature(string Role, string? AuthorName, string Content);
}

internal static class ChatMessageSourceIsolation
{
    // ChatMessage.Clone is shallow. Copy the dictionary as well because CompactionProvider
    // replaces source attribution entries while maintaining its incremental message index.
    public static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages) =>
        messages
            .Select(static message =>
            {
                var clone = message.Clone();
                if (message.AdditionalProperties != null)
                {
                    clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
                }

                return clone;
            })
            .ToList();
}
