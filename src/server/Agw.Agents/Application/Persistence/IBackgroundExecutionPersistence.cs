using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Application.Persistence;

public interface IBackgroundExecutionPersistence
{
    Task<BackgroundExecution?> FindAsync(Guid turnId, string ownerUserId, CancellationToken cancellationToken);

    void Stage(BackgroundExecution execution);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<bool> MarkRunningAsync(
        Guid turnId,
        string ownerUserId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken
    );

    Task<bool> MarkCancellationRequestedAsync(
        Guid turnId,
        string ownerUserId,
        string reason,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<BackgroundExecution>> ListNonTerminalAsync(CancellationToken cancellationToken);
}
