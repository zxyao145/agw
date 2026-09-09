using Agw.Agents.Execution.HumanInteraction.Application;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

internal sealed class MafPermissionState
{
    internal InteractionPermissionState Permissions { get; }

    public MafPermissionState(PermissionMode? permissionMode)
        : this(new InteractionPermissionState(permissionMode)) { }

    internal MafPermissionState(InteractionPermissionState permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Permissions = permissions;
    }

    public PermissionMode? Current => Permissions.Current;

    public void Register(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        MafSessionApprovalState.Synchronize(session, Permissions);
    }

    public void Set(PermissionMode? permissionMode) => Permissions.Set(permissionMode);
}
