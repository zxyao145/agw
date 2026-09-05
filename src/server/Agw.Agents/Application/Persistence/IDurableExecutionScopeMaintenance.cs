using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

public sealed record DurableExecutionScopeCursor(string UserId, Guid Id);

public sealed record DurableExecutionScopeBackfillResult(DurableExecutionScopeCursor? NextCursor, bool HasPending);

public interface IDurableExecutionScopeMaintenance
{
    // Uses the ambient owner filter. Seeding and scheduler scans may run in the restricted system scope.
    // One bounded pass. Background callers retain NextCursor across scopes/ticks; busy rows are retried next sweep.
    Task<DurableExecutionScopeBackfillResult> BackfillAsync(
        CancellationToken cancellationToken = default,
        DurableExecutionScopeCursor? after = null
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
