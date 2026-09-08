using System.Collections.Concurrent;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 保存当前 Server runtime 生命周期内可用的 InProcess checkpoint。
/// </summary>
internal sealed class AgentflowCheckpointRuntimeState
{
    private readonly ConcurrentDictionary<Guid, AgentflowCheckpointSnapshot> _snapshots = new();

    public IReadOnlySet<Guid> OccurrenceIds => _snapshots.Keys.ToHashSet();

    public void Register(AgentflowCheckpointSnapshot snapshot) => _snapshots[snapshot.OccurrenceId] = snapshot;

    public bool TryGet(Guid occurrenceId, out AgentflowCheckpointSnapshot? snapshot) =>
        _snapshots.TryGetValue(occurrenceId, out snapshot);

    public void RemoveAfter(long boundarySequence)
    {
        foreach (var snapshot in _snapshots.Values)
        {
            if (snapshot.BoundarySequence > boundarySequence)
            {
                _snapshots.TryRemove(snapshot.OccurrenceId, out _);
            }
        }
    }

    public void Clear() => _snapshots.Clear();
}
