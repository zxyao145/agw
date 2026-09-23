using Agw.Agents.Execution.Runtimes;

namespace Agw.Agents.Execution.Commands.Setting;

public static class SettingCommandMapper
{
    public static ExecutionSettings FromCommand(SettingCommand command) =>
        new(
            command.ProjectId,
            command.ContextId,
            command.EnvironmentVariables,
            command.PermissionMode,
            command.Resume,
            resultOnly: command.ResultOnly
        );
}
