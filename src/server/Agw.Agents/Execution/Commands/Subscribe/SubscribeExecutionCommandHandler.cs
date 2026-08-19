using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Subscribe;

/// <summary>
/// 将订阅命令转发到当前 ExecutionConnectionContext 的 durable session。
/// </summary>
public sealed class SubscribeExecutionCommandHandler : IExecutionCommandHandler<SubscribeExecutionCommand>
{
    /// <summary>
    /// 重新附着 execution，并替换当前 connection 的旧订阅。
    /// </summary>
    public Task HandleAsync(
        SubscribeExecutionCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    ) => context.SubscribeExecutionAsync(command.ExecutionId, command.Cursor, cancellationToken);
}
