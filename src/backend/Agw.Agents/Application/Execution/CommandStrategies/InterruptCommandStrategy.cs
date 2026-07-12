using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution.CommandStrategies;

internal sealed class InterruptCommandStrategy : IExecutionCommandStrategy
{
    public bool CanHandle(AgentRunCommand command) => command is InterruptCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var interruptCommand = (InterruptCommand)command;
        if (context.RuntimeSession is not { HasActiveTurn: true })
        {
            var message = string.IsNullOrWhiteSpace(interruptCommand.Reason)
                ? "No active request is currently running."
                : interruptCommand.Reason;
            await context.SendSystemMessageAsync(message);
            return default;
        }

        context.RuntimeSession.RequestInterrupt(interruptCommand.Reason);
        return default;
    }
}
