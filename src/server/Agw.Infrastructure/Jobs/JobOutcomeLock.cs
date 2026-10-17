namespace Agw.Infrastructure.Jobs;

internal static class JobOutcomeLock
{
    public static string GetResourceName(Guid jobId) => $"job-outcome:{jobId:N}";
}
