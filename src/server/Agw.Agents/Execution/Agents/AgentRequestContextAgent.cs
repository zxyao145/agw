using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Stages the original request and forwards a transient, optionally memory-enriched copy to the inner Agent.
/// </summary>
internal sealed class AgentRequestContextAgent : DelegatingAIAgent
{
    private const string CurrentRequestHeading = "\n\n## Current Request\n\n";

    private readonly AgentRequestChatHistoryProvider _historyProvider;
    private readonly Func<CancellationToken, ValueTask<ChatMessage?>>? _createMemoryContextAsync;
    private readonly ILogger _logger;

    public AgentRequestContextAgent(
        AIAgent innerAgent,
        AgentRequestChatHistoryProvider historyProvider,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync,
        ILogger logger
    )
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(historyProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _historyProvider = historyProvider;
        _createMemoryContextAsync = createMemoryContextAsync;
        _logger = logger;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _historyProvider.StageRequest(safeSession, SelectPersistableRequestMessages(requestMessages));
        Exception? executionFailure = null;
        try
        {
            var forwardedMessages = await CreateForwardedMessagesAsync(requestMessages, cancellationToken)
                .ConfigureAwait(false);
            return await InnerAgent
                .RunAsync(forwardedMessages, safeSession, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _historyProvider.StageRequest(safeSession, SelectPersistableRequestMessages(requestMessages));
        Exception? executionFailure = null;
        IReadOnlyList<ChatMessage> forwardedMessages;
        try
        {
            forwardedMessages = await CreateForwardedMessagesAsync(requestMessages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
            throw;
        }

        IAsyncEnumerator<AgentResponseUpdate> enumerator;
        try
        {
            enumerator = InnerAgent
                .RunStreamingAsync(forwardedMessages, safeSession, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
            throw;
        }

        await using (enumerator)
        {
            try
            {
                while (true)
                {
                    AgentResponseUpdate update;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                        update = enumerator.Current;
                    }
                    catch (Exception exception)
                    {
                        executionFailure = exception;
                        throw;
                    }

                    yield return update;
                }
            }
            finally
            {
                await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> CreateForwardedMessagesAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        var forwardedMessages = requestMessages.Select(CloneForTransientForwarding).ToList();
        if (_createMemoryContextAsync == null)
        {
            return forwardedMessages;
        }

        var memoryMessage = await _createMemoryContextAsync(cancellationToken).ConfigureAwait(false);
        if (memoryMessage == null || string.IsNullOrWhiteSpace(memoryMessage.Text))
        {
            return forwardedMessages;
        }

        var requestIndex = forwardedMessages.FindIndex(message =>
            message.Role == ChatRole.User
            && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
        );
        if (requestIndex < 0)
        {
            return forwardedMessages;
        }

        var composite = forwardedMessages[requestIndex];
        composite.Contents = [new TextContent(memoryMessage.Text + CurrentRequestHeading), .. composite.Contents];
        composite = composite.WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            memoryMessage.GetAgentRequestMessageSourceId() ?? ConversationHistoryMetadata.UserMemorySourceId
        );
        ConversationHistoryMetadata.ExcludeFromPersistence(composite);
        forwardedMessages[requestIndex] = composite;
        return forwardedMessages;
    }

    private static ChatMessage CloneForTransientForwarding(ChatMessage message)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.ToList();
        if (message.AdditionalProperties != null)
        {
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        }
        ConversationHistoryMetadata.ExcludeFromPersistence(clone);
        return clone;
    }

    private static IReadOnlyList<ChatMessage> SelectPersistableRequestMessages(IEnumerable<ChatMessage> messages) =>
        messages.Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message)).ToList();

    private async Task PersistPendingWithoutMaskingExecutionFailureAsync(
        AgentSession session,
        Exception? executionFailure
    )
    {
        try
        {
            await _historyProvider.PersistPendingAsync(this, session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (executionFailure != null)
        {
            _logger.LogError(exception, "Agent request persistence failed while preserving an execution failure.");
        }
    }
}
