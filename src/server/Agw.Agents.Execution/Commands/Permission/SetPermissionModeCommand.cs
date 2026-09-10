using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Permission;

public sealed class SetPermissionModeCommand : AgentRunCommand
{
    public AgwPermissionMode? PermissionMode { get; set; }
}
