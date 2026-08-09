using Agw.Agents.Execution.Commands.Setting;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Turns;

internal static class ToolApprovalPermissionState
{
    internal const string PermissionModeStateKey = "Agw.ToolApproval.PermissionMode";
    internal const string ToolApprovalStateKey = "toolApprovalState";

    public static void Apply(AgentSession session, PermissionMode? permissionMode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!permissionMode.HasValue)
        {
            if (session.StateBag.TryGetValue<string>(PermissionModeStateKey, out _))
            {
                session.StateBag.TryRemoveValue(ToolApprovalStateKey);
                session.StateBag.TryRemoveValue(PermissionModeStateKey);
            }

            return;
        }

        var mode = permissionMode.Value.ToString();
        if (session.StateBag.TryGetValue<string>(PermissionModeStateKey, out var currentMode) &&
            string.Equals(currentMode, mode, StringComparison.Ordinal))
        {
            return;
        }

        session.StateBag.TryRemoveValue(ToolApprovalStateKey);
        session.StateBag.SetValue(PermissionModeStateKey, mode);
    }
}
