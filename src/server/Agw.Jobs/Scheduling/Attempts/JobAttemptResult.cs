namespace Agw.Jobs.Scheduling.Attempts;

/// <summary>
/// Describes whether the scheduler should enqueue the updated snapshot or discard it after an attempt.
/// </summary>
public abstract record JobAttemptResult
{
    private JobAttemptResult() { }

    /// <summary>
    /// Enqueues the supplied job snapshot for its next attempt.
    /// </summary>
    public sealed record Reschedule : JobAttemptResult
    {
        public Reschedule(ScheduledJob job)
        {
            Job = job;
        }

        public ScheduledJob Job { get; }
    }

    /// <summary>
    /// Removes the job from in-memory scheduling without another enqueue.
    /// </summary>
    public sealed record Drop : JobAttemptResult;
}
