using Microsoft.Extensions.AI;

namespace Agw.Shared.Contracts.Tasks;

public interface IConversationHistoryWriter
{
    Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
