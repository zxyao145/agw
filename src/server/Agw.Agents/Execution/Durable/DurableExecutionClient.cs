using System.Runtime.CompilerServices;

namespace Agw.Agents.Execution.Durable;

internal sealed class DurableExecutionClient : IDurableExecutionClient
{
    private readonly DurableExecutionCoordinator _coordinator;

    public DurableExecutionClient(DurableExecutionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task StartAsync(DurableExecutionRequest request, CancellationToken cancellationToken) =>
        _coordinator.StartAsync(request, cancellationToken);

    public Task<DurableExecutionOutcome> GetOutcomeAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    ) => _coordinator.GetOutcomeAsync(executionId, userId, cancellationToken);

    public Task<DurableExecutionOutcome> WaitForActionableOutcomeAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    ) => _coordinator.WaitForActionableOutcomeAsync(executionId, userId, cancellationToken);

    public async IAsyncEnumerable<DurableExecutionEvent> ReadAsync(
        Guid executionId,
        string userId,
        string? afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        _ = await _coordinator.GetOutcomeAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
        await foreach (
            var entry in _coordinator.ReadAsync(executionId, afterCursor, cancellationToken).ConfigureAwait(false)
        )
        {
            yield return new DurableExecutionEvent(entry.Cursor, entry.Message);
        }
    }

    public Task<bool> InterruptAsync(
        Guid executionId,
        string userId,
        string? reason,
        CancellationToken cancellationToken
    ) => _coordinator.InterruptAsync(executionId, userId, reason, cancellationToken);
}
