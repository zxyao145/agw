using Agw.Domain.Entities;

namespace Agw.Jobs.Services;

public interface IScheduledTaskTimeCalculator
{
    DateTimeOffset? GetNextRunTime(ScheduledTask task, DateTimeOffset now);
}
