using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Permission;

public sealed class SetPermissionModeCommand : AgentRunCommand
{
    public PermissionMode? PermissionMode { get; set; }
}
