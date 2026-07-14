using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Application.Services;

public interface IJobTimeCalculator
{
    DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now);
}
