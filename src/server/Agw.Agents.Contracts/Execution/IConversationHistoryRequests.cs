using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Execution;

/// <summary>Tracks original inputs separately from transient, memory-enriched SDK requests.</summary>
public interface IConversationHistoryRequests
{
    void StageRequest(AgentSession session, IReadOnlyList<ChatMessage> messages);
    ValueTask PersistPendingAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken);
}
