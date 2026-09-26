using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Setting;

public static class SettingCommandMapper
{
    public static ExecutionSettings FromCommand(SettingCommand command) =>
        new(
            command.ProjectId,
            command.ConversationId == Guid.Empty
                ? throw new AgwException(ErrorCodes.InvalidParam, "SettingCommand.conversationId is required.")
                : command.ConversationId,
            command.EnvironmentVariables,
            command.PermissionMode,
            command.Resume,
            resultOnly: command.ResultOnly
        );
}
