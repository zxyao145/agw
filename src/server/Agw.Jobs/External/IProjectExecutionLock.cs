namespace Agw.Jobs.External;

public interface IProjectExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken);
}
