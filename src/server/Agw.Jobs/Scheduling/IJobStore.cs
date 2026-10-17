using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Scheduling;

/// <summary>
/// Persists scheduler-specific queries, state transitions, and execution logs for jobs.
/// Callers use these operations instead of mutating scheduling state through a generic repository.
/// </summary>
public interface IJobStore
{
    Task<IReadOnlyList<Job>> ListRunningAsync(CancellationToken cancellationToken);

    Task<Job?> GetRunningAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Job>> PrefetchAsync(
        DateTimeOffset now,
        DateTimeOffset horizon,
        CancellationToken cancellationToken
    );

    Task<JobAttemptStartResult> TryStartAttemptAsync(ScheduledJob scheduledJob, CancellationToken cancellationToken);
}
