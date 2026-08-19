using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Mode;

public sealed class SetModeCommandHandler : IExecutionCommandHandler<SetModeCommand>
{
    public Task HandleAsync(
        SetModeCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        if (command.AgentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "SetModeCommand.agentId is required.");
        }

        var mode = command.Mode.Trim().ToLowerInvariant();
        if (mode is not ("plan" or "execute"))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "SetModeCommand.mode must be either 'plan' or 'execute'.");
        }

        return context.SetModeAsync(command.AgentId, mode, cancellationToken);
    }
}
