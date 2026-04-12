using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution.CommandStrategies;

public readonly record struct ExecutionCommandResult(bool CloseConnection = false);

public interface IExecutionCommandStrategy
{
    bool CanHandle(AgentRunCommand command);

    Task<ExecutionCommandResult> ExecuteAsync(AgentRunCommand command, ExecutionCommandContext context);
}
