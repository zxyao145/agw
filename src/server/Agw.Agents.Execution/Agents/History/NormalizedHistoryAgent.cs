using System.Runtime.CompilerServices;
using Agw.Projects.Contracts.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Execution.Agents.History;

internal sealed class NormalizedHistoryAgent : DelegatingAIAgent
{
    private readonly NormalizedChatHistoryProvider _history;
    private readonly ILogger _logger;
    private readonly Func<IAgentMessageAdapter<AgentResponseUpdate>> _createAdapter;

    internal NormalizedHistoryAgent(
        AIAgent inner,
        NormalizedChatHistoryProvider history,
        Func<IAgentMessageAdapter<AgentResponseUpdate>> createAdapter,
        ILogger? logger = null
    )
        : base(inner)
    {
        _history = history;
        _logger = logger ?? NullLogger.Instance;
        _createAdapter = createAdapter;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false)
        )
            updates.Add(update);
        // Normalized updates include replacements. The framework's concatenating aggregator is not used for history.
        return NormalizedResponseAggregation.Aggregate(updates);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var capture = await _history
            .BeginAsync(this, safeSession, input, _createAdapter(), true, cancellationToken)
            .ConfigureAwait(false);
        var completed = false;
        Exception? failure = null;
        using var innerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = InnerAgent
            .RunStreamingAsync(input, safeSession, options, innerCancellation.Token)
            .GetAsyncEnumerator(innerCancellation.Token);
        var disposed = false;
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                var handled = false;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    update = enumerator.Current;
                    if (capture != null)
                        handled = await capture.ProcessAsync(update, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }
                if (capture == null)
                    yield return update;
                else
                {
                    foreach (var normalized in capture.Drain())
                        yield return normalized;
                    // Usage remains on the existing telemetry path, once per SDK event.
                    if (!handled)
                        yield return update;
                    else if (update.Contents.OfType<UsageContent>().Any())
                        yield return new AgentResponseUpdate
                        {
                            Role = ChatRole.System,
                            AuthorName = update.AuthorName,
                            Contents = update.Contents.OfType<UsageContent>().Cast<AIContent>().ToList(),
                            AdditionalProperties = update.AdditionalProperties,
                        };
                }
            }
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                disposed = true;
            }
            if (capture != null)
            {
                await capture
                    .FinishAsync(ConversationMessageState.Completed, CancellationToken.None)
                    .ConfigureAwait(false);
                completed = true;
                foreach (var normalized in capture.Drain())
                    yield return normalized;
            }
            completed = true;
        }
        finally
        {
            try
            {
                // Dispose the SDK before closing its capture so final history callbacks still target this projection.
                if (!disposed)
                {
                    innerCancellation.Cancel();
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (failure != null)
                    {
                        _logger.LogError(exception, "SDK cleanup failed after an execution failure.");
                    }
                }
                if (capture != null && !completed)
                    await capture
                        .FinishAsync(
                            failure is not null and not OperationCanceledException
                                ? ConversationMessageState.Failed
                                : ConversationMessageState.Interrupted,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (failure != null)
            {
                _logger.LogError(exception, "History cleanup failed after an execution failure.");
            }
            finally
            {
                _history.End(safeSession);
            }
        }
    }
}
