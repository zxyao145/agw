namespace Agw.Jobs.Scheduling;

/// <summary>
/// Defines the fixed prefetch and retry timing used by the in-process scheduler.
/// </summary>
public static class JobSchedulingDefaults
{
    public static readonly TimeSpan PrefetchInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PrefetchWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
}
