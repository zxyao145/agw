using Agw.Domain.Entities;

namespace Agw.Jobs.Services;

public interface IScheduledTaskTimeCalculator
{
    DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now);
}
