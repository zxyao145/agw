namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 决定一个已受理的 Turn 在哪个进程、什么时候执行，以及执行到一半怎样恢复。
/// Decides where and when an accepted turn runs, and how it resumes midway.
/// </summary>
internal interface IExecutionCoordinator
{
    Task<ExecutionReceipt> StartAsync(ExecutionRequest request, CancellationToken cancellationToken);
}
