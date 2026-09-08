using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class BackgroundAgentApprovalMiddleware
{
    private readonly HumanInteractionContextAccessor? _humanInteractionContextAccessor;

    public BackgroundAgentApprovalMiddleware(HumanInteractionContextAccessor? humanInteractionContextAccessor)
    {
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
    }

    public async Task<AgentResponse> RejectNewApprovalAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        using var interactionScope = _humanInteractionContextAccessor?.Suppress();
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        ThrowIfApprovalRequested(response.Messages.SelectMany(static message => message.Contents));
        return response;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RejectNewApprovalStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var interactionScope = _humanInteractionContextAccessor?.Suppress();
        await foreach (
            var update in innerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            ThrowIfApprovalRequested(update.Contents);
            yield return update;
        }
    }

    private static void ThrowIfApprovalRequested(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<ToolApprovalRequestContent>().Any())
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "A background agent requested a new tool approval. Background tasks cannot pause for approval."
            );
        }
    }
}
