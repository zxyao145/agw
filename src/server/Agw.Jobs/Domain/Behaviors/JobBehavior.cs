using Agw.Jobs.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Cronos;

namespace Agw.Jobs.Domain.Behaviors;

public sealed class JobBehavior
{
    private const string MissingOwnerError = "The Job owner is missing.";

    private readonly Job _job;

    public JobBehavior(Job job)
    {
        _job = job;
    }

    /// <summary>
    /// <para>新任务从等待状态开始；触发器没有下一次运行时间时，任务停留在最远的时间点。</para>
    /// <para>A new job starts pending; when its trigger has no next run, the job waits at the farthest point in time.</para>
    /// </summary>
    public void Create(JobDefinition definition, DateTimeOffset now)
    {
        Apply(definition);
        _job.Status = JobStatus.Pending;
        _job.NextRunTime = ResolveNextRunTime(now);
    }

    /// <summary>
    /// <para>运行中的任务不能修改，Running 状态只能由调度器设置。</para>
    /// <para>A running job cannot change, and only the scheduler may set the Running status.</para>
    /// </summary>
    public void Update(JobDefinition definition, JobStatus status, bool recalculateSchedule, DateTimeOffset now)
    {
        EnsureMutable();
        if (status == JobStatus.Running)
        {
            throw new AgwException(
                ErrorCodes.JobActiveAttemptConflict,
                "Job Running status is owned by the scheduler."
            );
        }

        Apply(definition);
        _job.Status = status;
        if (recalculateSchedule)
        {
            _job.NextRunTime = ResolveNextRunTime(now);
        }
    }

    public void EnsureMutable()
    {
        if (_job.Status == JobStatus.Running || _job.ActiveExecutionId.HasValue)
        {
            throw new AgwException(ErrorCodes.JobActiveAttemptConflict);
        }
    }

    /// <summary>
    /// <para>一次性触发器只在未来时间有下一次运行；间隔必须大于零；Cron 使用标准格式并按 UTC 计算。</para>
    /// <para>A one-time trigger has a next run only in the future; an interval must be positive; cron uses the standard format in UTC.</para>
    /// </summary>
    public DateTimeOffset? GetNextRunTime(DateTimeOffset now) =>
        _job.TriggerType switch
        {
            TriggerType.Once => GetNextOnceRunTime(now),
            TriggerType.Interval => now.Add(ParseInterval()),
            TriggerType.Cron => ParseCron().GetNextOccurrence(now, TimeZoneInfo.Utc),
            _ => throw new AgwException(
                ErrorCodes.UnsupportedTriggerType,
                $"Unsupported trigger type: {_job.TriggerType}"
            ),
        };

    public bool IsActiveAttempt(Guid executionId) =>
        _job.Status == JobStatus.Running
        && _job.ActiveExecutionId == executionId
        && _job.ActiveAttemptStartedAt.HasValue;

    public int GetCurrentAttempt() => _job.RetryCount + 1;

    /// <summary>
    /// <para>成功的尝试清除重试记录；启用且还有下一次运行的任务回到等待状态，否则暂停并停用。</para>
    /// <para>A successful attempt clears the retry record; an enabled job with a next run waits again, otherwise it pauses and is disabled.</para>
    /// </summary>
    public bool TryRescheduleAfterSuccess(DateTimeOffset now)
    {
        var nextRunTime = _job.IsEnabled ? GetNextRunTime(now) : null;
        _job.RetryCount = 0;
        _job.LastError = null;
        return TryScheduleNextAttempt(nextRunTime);
    }

    /// <summary>
    /// <para>失败的尝试计入重试次数；启用且没有超过最大重试次数的任务在 retryAt 重试，否则暂停并停用。</para>
    /// <para>A failed attempt counts as a retry; an enabled job within its retry limit retries at retryAt, otherwise it pauses and is disabled.</para>
    /// </summary>
    public bool TryRescheduleAfterFailure(string errorMessage, DateTimeOffset retryAt)
    {
        var retryCount = _job.RetryCount + 1;
        _job.RetryCount = retryCount;
        _job.LastError = errorMessage;
        return TryScheduleNextAttempt(_job.IsEnabled && retryCount <= _job.MaxRetryCount ? retryAt : null);
    }

    /// <summary>
    /// <para>没有所有者的任务无法以任何用户身份运行：本次尝试计为失败，任务暂停并停用。</para>
    /// <para>A job without an owner cannot run as any user: the attempt counts as failed and the job pauses and is disabled.</para>
    /// </summary>
    public void PauseForMissingOwner()
    {
        _job.RetryCount = GetCurrentAttempt();
        _job.LastError = MissingOwnerError;
        TryScheduleNextAttempt(null);
    }

    private void Apply(JobDefinition definition)
    {
        _job.ProjectId = definition.ProjectId;
        _job.AgentType = definition.AgentType;
        _job.AgentId = definition.AgentId;
        _job.Name = definition.Name;
        _job.Prompt = definition.Prompt;
        _job.TriggerType = definition.TriggerType;
        _job.TriggerValue = definition.TriggerValue;
        _job.MaxRetryCount = definition.MaxRetryCount;
        _job.IsEnabled = definition.IsEnabled;
    }

    private DateTimeOffset ResolveNextRunTime(DateTimeOffset now) => GetNextRunTime(now) ?? DateTimeOffset.MaxValue;

    private DateTimeOffset? GetNextOnceRunTime(DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(_job.TriggerValue, out var onceRunTime))
        {
            throw new AgwException(
                ErrorCodes.InvalidOnceTriggerValue,
                $"Invalid once trigger value: {_job.TriggerValue}"
            );
        }

        return onceRunTime > now ? onceRunTime : null;
    }

    private TimeSpan ParseInterval()
    {
        if (!TimeSpan.TryParse(_job.TriggerValue, out var interval) || interval <= TimeSpan.Zero)
        {
            throw new AgwException(
                ErrorCodes.InvalidIntervalTriggerValue,
                $"Invalid interval trigger value: {_job.TriggerValue}"
            );
        }

        return interval;
    }

    private CronExpression ParseCron()
    {
        try
        {
            return CronExpression.Parse(_job.TriggerValue, CronFormat.Standard);
        }
        catch (CronFormatException exception)
        {
            throw new AgwException(
                ErrorCodes.InvalidCronTriggerValue,
                $"Invalid cron trigger value: {_job.TriggerValue}",
                exception
            );
        }
    }

    private bool TryScheduleNextAttempt(DateTimeOffset? nextRunTime)
    {
        _job.ActiveExecutionId = null;
        _job.ActiveAttemptStartedAt = null;
        if (nextRunTime is { } runAt)
        {
            _job.Status = JobStatus.Pending;
            _job.NextRunTime = runAt;
            return true;
        }

        _job.Status = JobStatus.Paused;
        _job.IsEnabled = false;
        return false;
    }
}
