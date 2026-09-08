using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Execution;

/// <summary>Captures a model response before the SDK's completed-message callback.</summary>
public interface IStreamingConversationHistoryProvider
{
    ValueTask<IStreamingConversationHistory?> BeginStreamingResponseAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    );
}

public interface IStreamingConversationHistory
{
    ValueTask AppendAsync(ChatResponseUpdate update, CancellationToken cancellationToken);
    ValueTask CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}
