namespace Agw.Events.Application.Persistence;

public interface IEventIngestTransaction
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
