using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution.CommandStrategies;

internal sealed class HumanResponseCommandStrategy : IExecutionCommandStrategy
{
    public bool CanHandle(AgentRunCommand command) => command is HumanResponseCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var humanResponseCommand = (HumanResponseCommand)command;
        if (!context.ConnectionState.HasRunningExecution || context.ConnectionState.ActiveExecution == null)
        {
            await context.SendSystemMessageAsync("No active workflow is waiting for human approval.");
            return default;
        }

        var accepted = await context.ConnectionState.ActiveExecution.TrySubmitHumanResponseAsync(
            humanResponseCommand,
            context.CancellationToken);
        if (!accepted)
        {
            await context.SendSystemMessageAsync("No matching HumanGate request is waiting for this response.");
        }

        return default;
    }
}
