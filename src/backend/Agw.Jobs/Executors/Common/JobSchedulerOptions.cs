namespace Agw.Jobs.Executors.Common;

public sealed class JobSchedulerOptions
{
    public TimeSpan PrefetchInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan PrefetchWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DispatchRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
