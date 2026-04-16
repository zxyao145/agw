using Agw.Jobs.Executors.Common;

namespace Agw.Jobs.Executors.Cluster;

internal sealed class RedisJobDispatchResponse
{
    public string DispatchId { get; set; } = string.Empty;

    public string WorkerId { get; set; } = string.Empty;

    public Guid JobId { get; set; }

    public bool Succeeded { get; set; }

    public string? ErrorMessage { get; set; }

    public JobWorkerExecutionResult ExecutionResult { get; set; } = JobWorkerExecutionResult.Remove(Guid.Empty);
}
