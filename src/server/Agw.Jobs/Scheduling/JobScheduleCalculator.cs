using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Cronos;

namespace Agw.Jobs.Scheduling;

/// <summary>
/// Calculates the next UTC occurrence for one-time, interval, and cron job triggers.
/// </summary>
public sealed class JobScheduleCalculator
{
    public DateTimeOffset? GetNextRunTime(Job job, DateTimeOffset now)
    {
        switch (job.TriggerType)
        {
            case TriggerType.Once:
                if (!DateTimeOffset.TryParse(job.TriggerValue, out var onceRunTime))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidOnceTriggerValue,
                        $"Invalid once trigger value: {job.TriggerValue}"
                    );
                }

                return onceRunTime > now ? onceRunTime : null;

            case TriggerType.Interval:
                if (!TimeSpan.TryParse(job.TriggerValue, out var interval) || interval <= TimeSpan.Zero)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidIntervalTriggerValue,
                        $"Invalid interval trigger value: {job.TriggerValue}"
                    );
                }

                return now.Add(interval);

            case TriggerType.Cron:
                var cron = CronExpression.Parse(job.TriggerValue, CronFormat.Standard);
                return cron.GetNextOccurrence(now, TimeZoneInfo.Utc);

            default:
                throw new AgwException(
                    ErrorCodes.UnsupportedTriggerType,
                    $"Unsupported trigger type: {job.TriggerType}"
                );
        }
    }
}
