using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Application.Services;

public interface IAgentExecutor
{
    Task ExecuteAsync(Job task, CancellationToken cancellationToken);
}
