using Agw.Shared.Contracts.Coordination;

namespace Agw.Shared.Coordination;

public sealed class ApplicationLockScope : IAsyncDisposable
{
    private readonly IApplicationLockLease _lease;
    private readonly CancellationTokenSource _cancellation;

    public ApplicationLockScope(IApplicationLockLease lease, CancellationToken cancellationToken)
    {
        _lease = lease;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
    }

    public CancellationToken Token => _cancellation.Token;

    public async ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        await _lease.DisposeAsync();
    }
}
