namespace Agw.Agents.Execution.HumanInteraction.Application;

internal sealed record InteractionPermissionSnapshot(AgwPermissionMode? Mode, long Version);

internal sealed class InteractionPermissionState
{
    private InteractionPermissionSnapshot _snapshot;

    public InteractionPermissionState(AgwPermissionMode? mode, Guid? executionId = null, long version = 0)
    {
        ScopeId = executionId ?? Guid.CreateVersion7();
        _snapshot = new(mode, version);
    }

    public Guid ScopeId { get; }
    public InteractionPermissionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public AgwPermissionMode? Current => Snapshot.Mode;

    public void Set(AgwPermissionMode? mode, long? version = null)
    {
        while (true)
        {
            var previous = Snapshot;
            if (version.HasValue ? version.Value <= previous.Version : previous.Mode == mode)
                return;
            var next = new InteractionPermissionSnapshot(mode, version ?? checked(previous.Version + 1));
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, previous), previous))
                return;
        }
    }
}
