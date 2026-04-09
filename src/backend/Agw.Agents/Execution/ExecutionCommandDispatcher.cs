using System.Net.WebSockets;

using Agw.Api.Contracts;

namespace Agw.Api.Execution;

public readonly record struct ExecutionCommandResult(bool CloseConnection = false);

public interface IExecutionCommandStrategy
{
    bool CanHandle(AgentRunCommand command);

    Task<ExecutionCommandResult> ExecuteAsync(AgentRunCommand command, ExecutionCommandContext context);
}

public sealed class ExecutionCommandDispatcher(IEnumerable<IExecutionCommandStrategy> strategies)
{
    private readonly IReadOnlyList<IExecutionCommandStrategy> _strategies = strategies.ToArray();

    public async Task<ExecutionCommandResult> DispatchAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(command))
            {
                return await strategy.ExecuteAsync(command, context);
            }
        }

        await context.CloseConnectionAsync(WebSocketCloseStatus.InvalidPayloadData, "Not Support Payload");
        return new ExecutionCommandResult(CloseConnection: true);
    }
}
