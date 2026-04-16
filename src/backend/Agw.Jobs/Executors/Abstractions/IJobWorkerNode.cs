namespace Agw.Jobs.Executors.Abstractions;

public interface IJobWorkerNode
{
    Task RegisterAsync(CancellationToken cancellationToken);

    Task RunAsync(CancellationToken cancellationToken);

    Task UnregisterAsync(CancellationToken cancellationToken);
}
