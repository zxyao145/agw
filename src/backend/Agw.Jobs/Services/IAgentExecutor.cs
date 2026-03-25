using Agw.Domain.Entities;

namespace Agw.Jobs.Services;

public interface IAgentExecutor
{
    Task ExecuteAsync(Job task, CancellationToken cancellationToken);
}
