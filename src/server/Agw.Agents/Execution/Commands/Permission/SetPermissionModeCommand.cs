using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Commands.Setting;

namespace Agw.Agents.Execution.Commands.Permission;

public sealed class SetPermissionModeCommand : AgentRunCommand
{
    public PermissionMode? PermissionMode { get; set; }
}
