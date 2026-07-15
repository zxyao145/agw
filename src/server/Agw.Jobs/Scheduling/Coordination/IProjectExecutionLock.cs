namespace Agw.Jobs.Scheduling.Coordination;

/// <summary>
/// Serializes scheduled job execution for the same project across the configured lock scope.
/// </summary>
public interface IProjectExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken);
}
