using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Application.Services;

public interface IAgentExecutor
{
    Task<Guid> ExecuteAsync(Job task, CancellationToken cancellationToken);
}
