using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Hitl;

public sealed class HumanResponseCommandHandler : IExecutionCommandHandler<HumanResponseCommand>
{
    public Task HandleAsync(
        HumanResponseCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            command.Response is null
            || string.IsNullOrWhiteSpace(command.Response.InteractionId)
            || command.Response.InteractionId.Length > 128
        )
            throw new AgwException(ErrorCodes.InvalidParam, "A valid interaction response is required.");
        return context.SubmitHumanDecisionAsync(command, cancellationToken);
    }
}
