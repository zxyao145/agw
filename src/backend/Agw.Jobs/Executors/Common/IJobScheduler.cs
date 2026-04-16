namespace Agw.Jobs.Executors.Common;

public interface IJobScheduler
{
    Task RunAsync(CancellationToken cancellationToken);
}
