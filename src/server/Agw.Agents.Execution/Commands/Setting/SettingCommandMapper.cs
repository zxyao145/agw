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
            resultOnly: command.ResultOnly,
            extensionId: ValidateExtensionId(command.ExtensionId)
        );

    // extension 的 id 是小写的 GUID v7（RFC 9562 variant）；没有值时连接代表宿主。ToString("D") 输出小写，与原值相等即为小写的规范形式。
    // An extension id is a lowercase GUID v7 (RFC 9562 variant); no value means the host. ToString("D") is lowercase, so equality means the lowercase canonical form.
    private static string? ValidateExtensionId(string? extensionId)
    {
        if (extensionId == null)
        {
            return null;
        }

        if (
            !Guid.TryParseExact(extensionId, "D", out var guid)
            || guid.Version != 7
            || (guid.Variant & 0xC) != 0x8
            || !string.Equals(guid.ToString("D"), extensionId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "SettingCommand.extensionId must be a lowercase GUID v7.");
        }

        return extensionId;
    }
}
