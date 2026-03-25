using Agw.Domain.Entities;

namespace Agw.Jobs.Services;

public interface IAgentExecutor
{
    Task ExecuteAsync(ScheduledTask task, CancellationToken cancellationToken);
}
