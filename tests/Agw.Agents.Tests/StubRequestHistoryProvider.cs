using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

// Composition tests inspect wrapping and logging; persistence is tested with the EF provider.
internal sealed class StubRequestHistoryProvider : ChatHistoryProvider, IConversationHistoryRequests
{
    public void StageRequest(AgentSession session, IReadOnlyList<ChatMessage> messages) { }

    public ValueTask PersistPendingAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
