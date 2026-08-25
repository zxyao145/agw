using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Execution;

public interface IConversationHistoryWriter
{
    Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default
    );
}
