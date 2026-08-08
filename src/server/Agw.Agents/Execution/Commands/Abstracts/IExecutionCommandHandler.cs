using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Abstracts;

public interface IExecutionCommandHandler<in TCommand>
    where TCommand : AgentRunCommand
{
    Task HandleAsync(
        TCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken);
}

internal interface IExecutionCommandHandler
{
    Type CommandType { get; }

    Task HandleAsync(
        AgentRunCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken);
}

internal sealed class ExecutionCommandHandlerAdapter<TCommand> : IExecutionCommandHandler
    where TCommand : AgentRunCommand
{
    private readonly IExecutionCommandHandler<TCommand> _handler;

    public ExecutionCommandHandlerAdapter(IExecutionCommandHandler<TCommand> handler)
    {
        _handler = handler;
    }

    public Type CommandType => typeof(TCommand);

    public Task HandleAsync(
        AgentRunCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken) =>
        _handler.HandleAsync((TCommand)command, context, cancellationToken);
}
