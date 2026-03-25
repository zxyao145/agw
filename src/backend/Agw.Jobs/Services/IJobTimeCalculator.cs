using Agw.Domain.Entities;

namespace Agw.Jobs.Services;

public interface IJobTimeCalculator
{
    DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now);
}
