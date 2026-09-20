using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

public interface IDurableExecutionScopeMaintenance
{
    Task<bool> IsSessionCurrentAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    );

    // Mutates corrupt records before checking for active executions. This is not a read-only predicate.
    Task<bool> RepairAndCheckActiveExecutionsAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    // The worker must hold the execution lock throughout validation and segment execution.
    Task<bool> ValidateLockedExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

    // Returns an untracked, decrypted record for segment claiming. Caller holds the execution lock.
    Task<DurableExecutionRecord?> LoadValidatedExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default
    );
}
