namespace Agw.Jobs.Executors.Abstractions;

public interface IJobSchedulerCoordinator
{
    Task RunAsync(Func<CancellationToken, Task> scheduler, CancellationToken cancellationToken);
}
