namespace Agw.Jobs.Scheduling.Attempts;

public abstract record JobAttemptStartResult
{
    private JobAttemptStartResult() { }

    public sealed record Claimed(JobAttemptClaim Claim) : JobAttemptStartResult;

    public sealed record Reschedule(ScheduledJob Job) : JobAttemptStartResult;

    public sealed record Drop : JobAttemptStartResult;
}
