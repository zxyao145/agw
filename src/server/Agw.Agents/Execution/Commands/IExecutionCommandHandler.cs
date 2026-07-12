using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;

namespace Agw.Agents.Execution.Commands;

public interface IExecutionCommandHandler
{
    Type CommandType { get; }

    Task HandleAsync(
        AgentRunCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken);
}

public abstract class ExecutionCommandHandler<TCommand> : IExecutionCommandHandler
    where TCommand : AgentRunCommand
{
    public Type CommandType => typeof(TCommand);

    public Task HandleAsync(
        AgentRunCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken) =>
        HandleAsync((TCommand)command, connection, cancellationToken);

    protected abstract Task HandleAsync(
        TCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken);
}
