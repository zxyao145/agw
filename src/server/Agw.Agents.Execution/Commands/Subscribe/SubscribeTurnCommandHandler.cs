using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Inbound.Connections;

namespace Agw.Agents.Execution.Commands.Subscribe;

/// <summary>
/// 将订阅命令转发到当前 ExecutionConnectionContext 的 durable attachment。
/// </summary>
public sealed class SubscribeTurnCommandHandler : IExecutionCommandHandler<SubscribeTurnCommand>
{
    /// <summary>
    /// 重新附着 Turn，并替换当前 connection 的旧订阅。
    /// </summary>
    public Task HandleAsync(
        SubscribeTurnCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    ) => context.SubscribeTurnAsync(command.TurnId, command.Cursor, cancellationToken);
}
