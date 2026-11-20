namespace Agw.Events.Application.Persistence;

public interface IEventIngestTransaction
{
    Task AcquireLockAsync(string resource, CancellationToken cancellationToken);

    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
