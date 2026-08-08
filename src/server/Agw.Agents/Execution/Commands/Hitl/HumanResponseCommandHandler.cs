using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Hitl;

public sealed class HumanResponseCommandHandler : IExecutionCommandHandler<HumanResponseCommand>
{
    public Task HandleAsync(
        HumanResponseCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken) =>
        context.SubmitHumanDecisionAsync(command, cancellationToken);
}
