using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands;

internal sealed class ExecutionCommandDispatcher
{
    private readonly IReadOnlyDictionary<Type, IExecutionCommandHandler> _handlers;

    public ExecutionCommandDispatcher(IEnumerable<IExecutionCommandHandler> handlers)
    {
        var handlerMap = new Dictionary<Type, IExecutionCommandHandler>();
        foreach (var handler in handlers)
        {
            if (!handlerMap.TryAdd(handler.CommandType, handler))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Multiple execution handlers are registered for '{handler.CommandType.Name}'.");
            }
        }

        _handlers = handlerMap;
    }

    public Task DispatchAsync(
        AgentRunCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_handlers.TryGetValue(command.GetType(), out var handler))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Unsupported execution command.");
        }

        return handler.HandleAsync(command, context, cancellationToken);
    }
}
