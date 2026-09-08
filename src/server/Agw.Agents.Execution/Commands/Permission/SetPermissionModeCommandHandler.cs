using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Permission;

public sealed class SetPermissionModeCommandHandler : IExecutionCommandHandler<SetPermissionModeCommand>
{
    public Task HandleAsync(
        SetPermissionModeCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        if (!command.PermissionMode.HasValue || !Enum.IsDefined(command.PermissionMode.Value))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "SetPermissionModeCommand.permissionMode is required.");
        }

        return context.SetPermissionModeAsync(command.PermissionMode.Value, cancellationToken);
    }
}
