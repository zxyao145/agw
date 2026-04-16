namespace Agw.Jobs.Executors.Common;

public sealed record JobWorkerExecutionResult(
    Guid JobId,
    DateTimeOffset? NextRunTime,
    int RetryCount,
    bool RemoveFromSchedule)
{
    public static JobWorkerExecutionResult Schedule(Guid jobId, DateTimeOffset nextRunTime, int retryCount)
    {
        return new JobWorkerExecutionResult(jobId, nextRunTime, retryCount, RemoveFromSchedule: false);
    }

    public static JobWorkerExecutionResult Remove(Guid jobId)
    {
        return new JobWorkerExecutionResult(jobId, NextRunTime: null, RetryCount: 0, RemoveFromSchedule: true);
    }
}
