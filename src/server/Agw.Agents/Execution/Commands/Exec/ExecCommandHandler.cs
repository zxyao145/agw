using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Exec;

public sealed class ExecCommandHandler : IExecutionCommandHandler<ExecCommand>
{
    public Task HandleAsync(
        ExecCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!command.AgentId.HasValue || command.AgentId.Value == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }

        return context.StartTurnAsync(command, cancellationToken);
    }
}
