using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

public interface IAgentflowCheckpointPersistence
{
    Task BackfillExecutionScopesAsync(CancellationToken cancellationToken = default);

    Task<bool> RepairAndCheckActiveExecutionsAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    Task<TResult> ExecuteAsync<TResult>(
        Func<
            IAgentflowCheckpointPersistenceSession,
            CancellationToken,
            Task<AgentflowCheckpointPersistenceResult<TResult>>
        > operation,
        CancellationToken cancellationToken = default
    );

    Task<AgentflowCheckpointRecord?> FindCheckpointAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default
    );

    Task<bool> ProjectConversationExistsAsync(
        Guid projectId,
        Guid conversationId,
        string contextId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );
}

public sealed record AgentflowCheckpointPersistenceResult<TResult>(TResult Result, bool Commit);

public interface IAgentflowCheckpointPersistenceSession
{
    IAgentsDbContext Agents { get; }

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
