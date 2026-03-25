using Agw.Domain.Entities;
using Agw.Jobs.Enums;
using Cronos;

namespace Agw.Jobs.Services;

public class JobTimeCalculator : IJobTimeCalculator
{
    public DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now)
    {
        switch (task.TriggerType)
        {
            case TriggerType.Once:
                return null;
            case TriggerType.Interval:
                if (!TimeSpan.TryParse(task.TriggerValue, out var interval) || interval <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException($"Invalid interval trigger value: {task.TriggerValue}");
                }

                return now.Add(interval);
            case TriggerType.Cron:
                var cron = CronExpression.Parse(task.TriggerValue, CronFormat.Standard);
                var timezone = GetTimeZone(task.TimeZoneId);
                return cron.GetNextOccurrence(now, timezone);
            default:
                throw new NotSupportedException($"Unsupported trigger type: {task.TriggerType}");
        }
    }

    private static TimeZoneInfo GetTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }
}
