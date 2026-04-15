namespace Agw.Jobs.Application.Services;

public interface IProjectExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken);
}
