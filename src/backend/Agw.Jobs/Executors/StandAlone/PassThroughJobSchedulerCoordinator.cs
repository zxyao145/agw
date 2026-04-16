using Agw.Jobs.Executors.Abstractions;

namespace Agw.Jobs.Executors.StandAlone;

public sealed class PassThroughJobSchedulerCoordinator : IJobSchedulerCoordinator
{
    public Task RunAsync(Func<CancellationToken, Task> scheduler, CancellationToken cancellationToken)
    {
        return scheduler(cancellationToken);
    }
}
