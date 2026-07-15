using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;

namespace Agw.Jobs.Tests;

public class JobSchedulerWakeSignalTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NotifyCreated_UpcomingEnabledOnceJob_ReleasesWaiter()
    {
        var signal = new JobSchedulerWakeSignal(new TestTimeProvider(UtcNow));
        var wait = signal.WaitAsync(TestContext.Current.CancellationToken);

        signal.NotifyCreated(CreateJob(TriggerType.Once, UtcNow.AddSeconds(30), true, JobStatus.Pending));

        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(TriggerType.Interval, true, JobStatus.Pending)]
    [InlineData(TriggerType.Once, false, JobStatus.Pending)]
    [InlineData(TriggerType.Once, true, JobStatus.Paused)]
    public async Task NotifyCreated_NonUrgentJob_DoesNotReleaseWaiter(
        TriggerType triggerType,
        bool isEnabled,
        JobStatus status)
    {
        var signal = new JobSchedulerWakeSignal(new TestTimeProvider(UtcNow));
        using var cancellation = new CancellationTokenSource();
        var wait = signal.WaitAsync(cancellation.Token);

        signal.NotifyCreated(CreateJob(triggerType, UtcNow.AddSeconds(30), isEnabled, status));

        Assert.False(wait.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task NotifyCreated_OnceJobAtPrefetchInterval_DoesNotReleaseWaiter()
    {
        var signal = new JobSchedulerWakeSignal(new TestTimeProvider(UtcNow));
        using var cancellation = new CancellationTokenSource();
        var wait = signal.WaitAsync(cancellation.Token);

        signal.NotifyCreated(CreateJob(
            TriggerType.Once,
            UtcNow.Add(JobSchedulingDefaults.PrefetchInterval),
            true,
            JobStatus.Pending));

        Assert.False(wait.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private static Job CreateJob(
        TriggerType triggerType,
        DateTimeOffset nextRunTime,
        bool isEnabled,
        JobStatus status)
    {
        return new Job
        {
            TriggerType = triggerType,
            NextRunTime = nextRunTime,
            IsEnabled = isEnabled,
            Status = status
        };
    }
}
