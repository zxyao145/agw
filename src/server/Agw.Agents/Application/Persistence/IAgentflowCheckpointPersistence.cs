namespace Agw.Agents.Application.Persistence;

public interface IAgentflowCheckpointPersistence
{
    Task<TResult> ExecuteAsync<TResult>(
        Func<IAgentflowCheckpointPersistenceSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default
    );
}

public interface IAgentflowCheckpointPersistenceSession
{
    IAgentsDbContext Agents { get; }

    Task<bool> ProjectConversationExistsAsync(
        Guid projectId,
        Guid conversationId,
        string contextId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    Task<long> GetLastConversationSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default);

    void AddConversationHistory(AgentflowCheckpointHistoryWrite history);

    Task DeleteConversationHistoryAfterAsync(
        Guid conversationId,
        long boundarySequence,
        CancellationToken cancellationToken = default
    );
}

public sealed record AgentflowCheckpointHistoryWrite(
    Guid Id,
    Guid ConversationId,
    Guid TaskId,
    string? AgentName,
    long ConversationSequence,
    string ConversationPayload,
    DateTimeOffset Timestamp
);
