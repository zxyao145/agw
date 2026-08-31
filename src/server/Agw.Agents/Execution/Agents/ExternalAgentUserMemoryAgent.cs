using System.Runtime.CompilerServices;
using Agw.Agents.ExternalAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Injects user-memory context into External Agent prompts without making that context part of persisted chat history.
/// </summary>
internal sealed class ExternalAgentUserMemoryAgent : DelegatingAIAgent, IAsyncDisposable
{
    private const string CurrentRequestHeading = "\n\n## Current Request\n\n";

    private readonly AIContextProvider _userMemoryProvider;
    private readonly ExternalAgentKind _kind;
    private int _disposed;

    public ExternalAgentUserMemoryAgent(
        AIAgent innerAgent,
        AIContextProvider userMemoryProvider,
        ExternalAgentKind kind
    )
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(userMemoryProvider);
        _userMemoryProvider = userMemoryProvider;
        _kind = kind;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var enrichedMessages = await EnrichMessagesAsync(messages, session, cancellationToken).ConfigureAwait(false);
        return await InnerAgent.RunAsync(enrichedMessages, session, options, cancellationToken).ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var enrichedMessages = await EnrichMessagesAsync(messages, session, cancellationToken).ConfigureAwait(false);
        await foreach (
            var update in InnerAgent
                .RunStreamingAsync(enrichedMessages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        switch (InnerAgent)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> EnrichMessagesAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        CancellationToken cancellationToken
    )
    {
        var requestMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var currentRequest = requestMessages.FirstOrDefault(message =>
            message.Role == ChatRole.User
            && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
        );
        if (currentRequest == null)
        {
            return requestMessages;
        }

        var context = await _userMemoryProvider
            .InvokingAsync(new AIContextProvider.InvokingContext(this, session, new AIContext()), cancellationToken)
            .ConfigureAwait(false);
        var memoryMessages = context.Messages?.ToList() ?? [];
        if (memoryMessages.Count == 0)
        {
            return requestMessages;
        }

        if (_kind != ExternalAgentKind.ClaudeCode)
        {
            return [.. memoryMessages, .. requestMessages];
        }

        // Claude Code consumes the first User message as its prompt but forwards the complete request list
        // to its ChatHistoryProvider. Keep the original request for persistence and put the composite first.
        var memoryText = string.Join(
            "\n\n",
            memoryMessages.Select(message => message.Text).Where(text => !string.IsNullOrWhiteSpace(text))
        );
        if (string.IsNullOrWhiteSpace(memoryText))
        {
            return requestMessages;
        }

        var compositePrompt = currentRequest.Clone();
        compositePrompt.Contents = [new TextContent(memoryText + CurrentRequestHeading), .. currentRequest.Contents];
        if (currentRequest.AdditionalProperties != null)
        {
            compositePrompt.AdditionalProperties = new AdditionalPropertiesDictionary(
                currentRequest.AdditionalProperties
            );
        }

        compositePrompt = compositePrompt.WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            _userMemoryProvider.GetType().FullName
        );
        return [compositePrompt, .. requestMessages];
    }
}
