using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;

namespace Agw.Agents.Execution.Commands;

public sealed class HumanResponseCommandHandler : ExecutionCommandHandler<HumanResponseCommand>
{
    protected override async Task HandleAsync(
        HumanResponseCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.Runtime == null
            || !await connection.Runtime.TrySubmitHumanResponseAsync(command, cancellationToken))
        {
            await connection.SendSystemMessageAsync(
                "No matching HumanGate request is waiting for this response.");
        }
    }
}
