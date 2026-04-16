using Agw.Jobs.Dtos;

namespace Agw.Jobs.Executors.Common;

public interface IJobWorker
{
    Task<JobWorkerExecutionResult> ExecuteAsync(InMemoryJob job, CancellationToken cancellationToken);
}
