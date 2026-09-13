using Agw.Jobs.Scheduling.Attempts;

namespace Agw.Jobs.Application.Persistence;

/// <summary>Commits Project task completion and its Job outcome in one unit of work.</summary>
public interface IJobOutcomeTransaction
{
    Task<JobAttemptResult> ExecuteAsync(
        Guid jobId,
        Func<CancellationToken, Task<JobAttemptResult>> record,
        CancellationToken cancellationToken
    );
}
