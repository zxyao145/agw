using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Execution;

public interface IJobAgentExecutor
{
    Task<Guid> ExecuteAsync(Job job, CancellationToken cancellationToken);
}
