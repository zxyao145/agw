namespace Agw.Jobs.External;

public interface IProjectExecutionLock
{
    /// <summary>
    /// Acquires the execution lock for a project.
    /// </summary>
    /// <remarks>
    /// The <paramref name="cancellationToken" /> only cancels waiting for the lock to be acquired.
    /// After this method returns successfully, the caller must dispose the returned lease to release the lock.
    /// </remarks>
    Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken);
}
