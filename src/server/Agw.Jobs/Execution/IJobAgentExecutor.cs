using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Execution;

public interface IJobAgentExecutor
{
    Task ExecuteAsync(Job job, Guid turnId, CancellationToken cancellationToken);
}
