using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Application.Services;

public interface IJobTimeCalculator
{
    DateTimeOffset? GetNextRunTime(Job task, DateTimeOffset now);
}
