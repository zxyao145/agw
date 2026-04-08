using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Enums;
using Cronos;

namespace Agw.Jobs.Application.Services;

public class JobTimeCalculator : IJobTimeCalculator
{
    public DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now)
    {
        switch (task.TriggerType)
        {
            case TriggerType.Once:
                if (!DateTimeOffset.TryParse(task.TriggerValue, out var onceRunTime))
                {
                    throw new InvalidOperationException($"Invalid once trigger value: {task.TriggerValue}");
                }

                return onceRunTime > now ? onceRunTime : null;
            case TriggerType.Interval:
                if (!TimeSpan.TryParse(task.TriggerValue, out var interval) || interval <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException($"Invalid interval trigger value: {task.TriggerValue}");
                }

                return now.Add(interval);
            case TriggerType.Cron:
                var cron = CronExpression.Parse(task.TriggerValue, CronFormat.Standard);
                return cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
            default:
                throw new NotSupportedException($"Unsupported trigger type: {task.TriggerType}");
        }
    }
}
