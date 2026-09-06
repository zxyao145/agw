namespace Agw.Shared.Contracts.Coordination;

/// <summary>
/// Serializes application mutations that target the same logical resource.
/// </summary>
public interface IApplicationLock
{
    Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken);
}

public interface IApplicationLockLease : IAsyncDisposable
{
    CancellationToken HandleLostToken { get; }
}
