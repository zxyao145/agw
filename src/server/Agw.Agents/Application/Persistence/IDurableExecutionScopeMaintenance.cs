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

    // Quarantines a record whose manifest or scope is invalid; a concurrent state change wins through StateVersion.
    Task<bool> ValidateExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

    // Returns an untracked, decrypted record for a segment whose lease the caller has just claimed.
    Task<DurableExecutionRecord?> LoadValidatedExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default
    );
}
