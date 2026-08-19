namespace Agw.Shared.Contracts.Coordination;

/// <summary>
/// Serializes application mutations that target the same logical resource.
/// </summary>
public interface IApplicationLock
{
    Task<IAsyncDisposable> AcquireAsync(string resourceName, CancellationToken cancellationToken);
}
