using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Inbound.Connections;

namespace Agw.Agents.Execution.Commands.Checkpoint;

public sealed class ResumeCheckpointCommandHandler : IExecutionCommandHandler<ResumeCheckpointCommand>
{
    public Task HandleAsync(
        ResumeCheckpointCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    ) => context.ResumeCheckpointAsync(command, cancellationToken);
}
