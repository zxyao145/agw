using Agw.Jobs.Domain.Behaviors;
using Agw.Jobs.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Tests;

public class JobBehaviorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TriggerType.Once, "2026-07-15T09:00:00Z", "2026-07-15T09:00:00+00:00")]
    [InlineData(TriggerType.Interval, "00:15:00", "2026-07-15T08:15:00+00:00")]
    [InlineData(TriggerType.Cron, "15 8 * * *", "2026-07-15T08:15:00+00:00")]
    public void GetNextRunTime_ValidTrigger_ReturnsExpected(
        TriggerType triggerType,
        string triggerValue,
        string expected
    )
    {
        var job = new Job { TriggerType = triggerType, TriggerValue = triggerValue };

        var result = new JobBehavior(job).GetNextRunTime(UtcNow);

        Assert.Equal(DateTimeOffset.Parse(expected), result);
    }

    [Fact]
    public void GetNextRunTime_PastOnceTrigger_ReturnsNull()
    {
        var job = new Job { TriggerType = TriggerType.Once, TriggerValue = "2026-07-15T07:00:00Z" };

        var result = new JobBehavior(job).GetNextRunTime(UtcNow);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(TriggerType.Once, "not-a-date", 400_0032)]
    [InlineData(TriggerType.Interval, "00:00:00", 400_0033)]
    [InlineData(TriggerType.Cron, "99 * * * *", 400_0076)]
    [InlineData(TriggerType.Cron, "*/5 * * *", 400_0076)]
    public void GetNextRunTime_InvalidTrigger_ThrowsExpectedAgwException(
        TriggerType triggerType,
        string triggerValue,
        int expectedCode
    )
    {
        var job = new Job { TriggerType = triggerType, TriggerValue = triggerValue };

        var exception = Assert.Throws<AgwException>(() => new JobBehavior(job).GetNextRunTime(UtcNow));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Create_PastOnceTrigger_StartsPendingAtFarthestTime()
    {
        var job = new Job { Status = JobStatus.Paused };

        new JobBehavior(job).Create(CreateDefinition(TriggerType.Once, "2026-07-15T07:00:00Z"), UtcNow);

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(DateTimeOffset.MaxValue, job.NextRunTime);
        Assert.Equal("nightly", job.Name);
    }

    [Fact]
    public void Update_RunningJob_ThrowsActiveAttemptConflictWithoutChange()
    {
        var job = new Job { Name = "original", Status = JobStatus.Running };

        var exception = Assert.Throws<AgwException>(() =>
            new JobBehavior(job).Update(CreateDefinition(), JobStatus.Pending, true, UtcNow)
        );

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
        Assert.Equal("original", job.Name);
    }

    [Fact]
    public void Update_RequestedRunningStatus_ThrowsActiveAttemptConflict()
    {
        var job = new Job { Status = JobStatus.Pending };

        var exception = Assert.Throws<AgwException>(() =>
            new JobBehavior(job).Update(CreateDefinition(), JobStatus.Running, true, UtcNow)
        );

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
    }

    [Fact]
    public void Update_WithoutRecalculation_KeepsNextRunTime()
    {
        var scheduledAt = UtcNow.AddDays(1);
        var job = new Job { Status = JobStatus.Pending, NextRunTime = scheduledAt };

        new JobBehavior(job).Update(CreateDefinition(), JobStatus.Paused, false, UtcNow);

        Assert.Equal(scheduledAt, job.NextRunTime);
        Assert.Equal(JobStatus.Paused, job.Status);
    }

    [Fact]
    public void TryRescheduleAfterSuccess_RecurringJob_WaitsForNextRunAndClearsRetries()
    {
        var job = CreateRunningJob(TriggerType.Interval, "00:15:00");
        job.RetryCount = 2;
        job.LastError = "earlier failure";

        var rescheduled = new JobBehavior(job).TryRescheduleAfterSuccess(UtcNow);

        Assert.True(rescheduled);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(UtcNow.AddMinutes(15), job.NextRunTime);
        Assert.Equal(0, job.RetryCount);
        Assert.Null(job.LastError);
        Assert.Null(job.ActiveExecutionId);
        Assert.Null(job.ActiveAttemptStartedAt);
    }

    [Fact]
    public void TryRescheduleAfterSuccess_NoNextRun_PausesAndDisables()
    {
        var job = CreateRunningJob(TriggerType.Once, "2026-07-15T07:00:00Z");

        var rescheduled = new JobBehavior(job).TryRescheduleAfterSuccess(UtcNow);

        Assert.False(rescheduled);
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.False(job.IsEnabled);
        Assert.Null(job.ActiveExecutionId);
    }

    [Fact]
    public void TryRescheduleAfterFailure_WithinRetryLimit_RetriesAtGivenTime()
    {
        var job = CreateRunningJob(TriggerType.Interval, "00:15:00");
        var retryAt = UtcNow.AddSeconds(30);

        var rescheduled = new JobBehavior(job).TryRescheduleAfterFailure("boom", retryAt);

        Assert.True(rescheduled);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(retryAt, job.NextRunTime);
        Assert.Equal(1, job.RetryCount);
        Assert.Equal("boom", job.LastError);
    }

    [Fact]
    public void TryRescheduleAfterFailure_RetriesExhausted_PausesAndDisables()
    {
        var job = CreateRunningJob(TriggerType.Interval, "00:15:00");
        job.MaxRetryCount = 1;
        job.RetryCount = 1;

        var rescheduled = new JobBehavior(job).TryRescheduleAfterFailure("boom", UtcNow.AddSeconds(30));

        Assert.False(rescheduled);
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.False(job.IsEnabled);
        Assert.Equal(2, job.RetryCount);
    }

    [Fact]
    public void PauseForMissingOwner_CountsAttemptAndDisables()
    {
        var job = CreateRunningJob(TriggerType.Interval, "00:15:00");

        new JobBehavior(job).PauseForMissingOwner();

        Assert.Equal(1, job.RetryCount);
        Assert.Equal("The Job owner is missing.", job.LastError);
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.False(job.IsEnabled);
        Assert.Null(job.ActiveExecutionId);
    }

    [Fact]
    public void IsActiveAttempt_OtherExecution_ReturnsFalse()
    {
        var job = CreateRunningJob(TriggerType.Interval, "00:15:00");

        Assert.False(new JobBehavior(job).IsActiveAttempt(Guid.CreateVersion7()));
        Assert.True(new JobBehavior(job).IsActiveAttempt(job.ActiveExecutionId!.Value));
    }

    private static JobDefinition CreateDefinition(
        TriggerType triggerType = TriggerType.Interval,
        string triggerValue = "00:15:00"
    ) =>
        new()
        {
            ProjectId = Guid.CreateVersion7(),
            Name = "nightly",
            Prompt = "Summarize",
            TriggerType = triggerType,
            TriggerValue = triggerValue,
            MaxRetryCount = 3,
            IsEnabled = true,
        };

    private static Job CreateRunningJob(TriggerType triggerType, string triggerValue) =>
        new()
        {
            TriggerType = triggerType,
            TriggerValue = triggerValue,
            Status = JobStatus.Running,
            IsEnabled = true,
            MaxRetryCount = 3,
            ActiveExecutionId = Guid.CreateVersion7(),
            ActiveAttemptStartedAt = UtcNow.AddMinutes(-1),
        };
}
