using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;

namespace Agw.Agents.Execution.Commands;

public sealed class InterruptCommandHandler : ExecutionCommandHandler<InterruptCommand>
{
    protected override async Task HandleAsync(
        InterruptCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.Runtime is not { HasActiveTurn: true })
        {
            await connection.SendSystemMessageAsync(
                command.Reason ?? "No active request is currently running.");
            return;
        }

        connection.Runtime.RequestInterrupt();
    }
}
