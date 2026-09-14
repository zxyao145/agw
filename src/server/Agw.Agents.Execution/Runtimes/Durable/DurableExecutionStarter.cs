using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Shared.Runtime;

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
        using var workspaceScope =
            request.WorkspaceSnapshot == null
                ? null
                : ProjectWorkspaceContext.Push(request.Task.ProjectId, request.WorkspaceSnapshot);
        var command = ExecutionStartCommandMapper.Map(request);
        await _session.StartAsync(command, request.Task, request.Settings, cancellationToken);
        return new ExecutionReceipt(command.ExecutionId!.Value, true);
    }
}
