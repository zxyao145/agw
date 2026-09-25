using Agw.Agents.Execution.HumanInteraction.Application;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

internal sealed class MafPermissionState
{
    internal InteractionPermissionState Permissions { get; }

    public MafPermissionState(AgwPermissionMode? permissionMode)
        : this(new InteractionPermissionState(permissionMode)) { }

    internal MafPermissionState(InteractionPermissionState permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Permissions = permissions;
    }

    public AgwPermissionMode? Current => Permissions.Current;

    public void Register(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        MafSessionApprovalState.Synchronize(session, Permissions);
    }
}
