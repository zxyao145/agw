using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Steer;

public sealed class SteerCommandHandler : IExecutionCommandHandler<SteerCommand>
{
    public Task HandleAsync(
        SteerCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        if (command.ConversationId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam, "SteerCommand.conversationId is required.");
        if (command.TurnId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam, "SteerCommand.turnId is required.");
        if (command.SteerId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam, "SteerCommand.steerId is required.");
        if (command.Input is null || command.Input.Contents is null || command.Input.Contents.Count == 0)
            throw new AgwException(ErrorCodes.InvalidParam, "SteerCommand.input is required.");
        return context.SteerTurnAsync(command, cancellationToken);
    }
}
