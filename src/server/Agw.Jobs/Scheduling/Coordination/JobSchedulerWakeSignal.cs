using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Scheduling.Coordination;

/// <summary>
/// Wakes the scheduler prefetch loop when a newly created one-time job falls inside the
/// immediate prefetch interval.
/// </summary>
public sealed class JobSchedulerWakeSignal
{
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public JobSchedulerWakeSignal(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void NotifyCreated(Job job)
    {
        var now = _timeProvider.GetUtcNow();
        if (job.TriggerType == TriggerType.Once
            && job.IsEnabled
            && job.Status == JobStatus.Pending
            && job.NextRunTime < now.Add(JobSchedulingDefaults.PrefetchInterval))
        {
            _signal.Release();
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(cancellationToken);
    }
}
