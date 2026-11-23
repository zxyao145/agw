namespace Agw.Agents.Application.Persistence;

public interface IAgentConversationChangesWriter
{
    Task<int> SaveConversationChangesAsync(
        Guid conversationId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    );
}
