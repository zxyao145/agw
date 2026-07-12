using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Application.Services;

public interface IAgentExecutor
{
    Task<Guid> ExecuteAsync(Job task, CancellationToken cancellationToken);
}
