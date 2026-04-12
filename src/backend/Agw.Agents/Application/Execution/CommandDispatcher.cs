using System.Net.WebSockets;

using Agw.Agents.Application.Execution.CommandStrategies;
using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution;


public sealed class CommandDispatcher(IEnumerable<IExecutionCommandStrategy> strategies)
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
