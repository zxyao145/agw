using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Interrupt;

public sealed class InterruptCommandHandler : IExecutionCommandHandler<InterruptCommand>
{
    public Task HandleAsync(
        InterruptCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken) =>
        context.InterruptTurnAsync(command.ExecutionId, command.Reason, cancellationToken);
}
