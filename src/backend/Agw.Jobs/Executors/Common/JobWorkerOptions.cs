namespace Agw.Jobs.Executors.Common;

public sealed class JobWorkerOptions
{
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
