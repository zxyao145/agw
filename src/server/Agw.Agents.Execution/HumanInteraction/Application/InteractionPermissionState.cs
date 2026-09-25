namespace Agw.Agents.Execution.HumanInteraction.Application;

internal sealed record InteractionPermissionSnapshot(AgwPermissionMode? Mode, long Version);

/// <summary>
/// 一个 Turn 开始时的权限快照；Turn 期间保持不变，修改只作用于下一个 Turn。
/// The permission snapshot taken when a turn starts; it stays fixed for the turn, and changes apply to the next turn.
/// </summary>
internal sealed class InteractionPermissionState
{
    public InteractionPermissionState(AgwPermissionMode? mode, Guid? executionId = null, long version = 0)
    {
        ScopeId = executionId ?? Guid.CreateVersion7();
        Snapshot = new(mode, version);
    }

    public Guid ScopeId { get; }
    public InteractionPermissionSnapshot Snapshot { get; }
    public AgwPermissionMode? Current => Snapshot.Mode;
}
