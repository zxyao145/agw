namespace Agw.Jobs.Executors.Common;

public sealed class JobWorkerDispatchResult
{
    public JobWorkerDispatchResult(string workerId, Guid jobId)
        : this(workerId, jobId, JobWorkerExecutionResult.Remove(jobId))
    {
    }

    public JobWorkerDispatchResult(string workerId, Guid jobId, JobWorkerExecutionResult executionResult)
    {
        WorkerId = workerId;
        JobId = jobId;
        ExecutionResult = executionResult;
    }

    public string WorkerId { get; }

    public Guid JobId { get; }

    public JobWorkerExecutionResult ExecutionResult { get; }
}
