using Agw.Api.Contracts;

namespace Agw.Api.Execution;

internal sealed class InterruptCommandStrategy : IExecutionCommandStrategy
{
    public bool CanHandle(AgentRunCommand command) => command is InterruptCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var interruptCommand = (InterruptCommand)command;
        if (!context.ConnectionState.HasRunningExecution || context.ConnectionState.ActiveExecution == null)
        {
            var message = string.IsNullOrWhiteSpace(interruptCommand.Reason)
                ? "No active request is currently running."
                : interruptCommand.Reason;
            await context.SendSystemMessageAsync(message);
            return default;
        }

        context.ConnectionState.ActiveExecution.RequestInterrupt(interruptCommand.Reason);
        return default;
    }
}
