namespace Agw.Jobs.Executors.Abstractions;

public sealed class JobWorkerPoolOptions
{
    public string Mode { get; set; } = "SingleNode";

    public string? WorkerId { get; set; }

    public string? NodeId { get; set; }

    public int MaxConcurrentJobs { get; set; } = Math.Max(1, Environment.ProcessorCount);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan WorkerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan DispatchPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan DispatchResultTtl { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan SchedulerLockTtl { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan SchedulerLockRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan SchedulerLockRenewInterval { get; set; } = TimeSpan.FromSeconds(10);
}
