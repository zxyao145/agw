using System.Text.RegularExpressions;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Commands.Setting;

public static class SettingCommandMapper
{
    private static readonly Regex ExtensionIdRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z");

    public static ExecutionSettings FromCommand(SettingCommand command) =>
        new(
            command.ProjectId,
            command.ConversationId == Guid.Empty
                ? throw new AgwException(ErrorCodes.InvalidParam, "SettingCommand.conversationId is required.")
                : command.ConversationId,
            command.EnvironmentVariables,
            command.PermissionMode,
            command.Resume,
            resultOnly: command.ResultOnly,
            extensionId: ValidateExtensionId(command.ExtensionId)
        );

    // extension 的 id：3 到 64 个小写字母、数字与单个连字符，不能是保留值 host；没有值时连接代表宿主。
    // An extension id has 3 to 64 lowercase letters, digits and single hyphens and cannot be the reserved host value; no value means the host.
    private static string? ValidateExtensionId(string? extensionId)
    {
        if (extensionId == null)
        {
            return null;
        }

        if (string.Equals(extensionId, ConversationCreators.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "SettingCommand.extensionId cannot be \"host\".");
        }

        if (extensionId.Length is < 3 or > 64 || !ExtensionIdRegex.IsMatch(extensionId))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "SettingCommand.extensionId must have 3 to 64 lowercase letters, digits and single hyphens."
            );
        }

        return extensionId;
    }
}
