using Agw.Agents.Execution.Runtimes.Contracts;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 将启动登记和连接 attachment 交给 Durable Session，Worker 独立领取执行。
/// </summary>
internal sealed class DurableExecutionStarter : IExecutionStarter
{
    private readonly DurableExecutionSession _session;

    public DurableExecutionStarter(DurableExecutionSession session)
    {
        _session = session;
    }

    public async Task<ExecutionReceipt> StartAsync(ExecutionStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = ExecutionStartCommandMapper.Map(request);
        await _session.StartAsync(command, request.Task, request.Settings, cancellationToken);
        return new ExecutionReceipt(command.ExecutionId!.Value, true);
    }
}
