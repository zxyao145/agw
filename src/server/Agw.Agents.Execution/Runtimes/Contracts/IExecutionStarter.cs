namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 接受当前连接的一次执行启动；执行完成由消息和现有状态查询通知。
/// </summary>
internal interface IExecutionStarter
{
    Task<ExecutionReceipt> StartAsync(ExecutionStartRequest request, CancellationToken cancellationToken);
}
