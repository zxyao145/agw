namespace Agw.Agents.Definitions.Domain.Topology;

public static class AgentflowReferenceTopology
{
    public static bool HasCycle(
        Guid root,
        IReadOnlyCollection<Guid> candidateReferences,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> references
    )
    {
        var completed = new HashSet<Guid>();
        var path = new HashSet<Guid>();
        var pending = new Stack<(Guid Id, bool Leaving)>();
        pending.Push((root, false));
        while (pending.TryPop(out var next))
        {
            if (next.Leaving)
            {
                path.Remove(next.Id);
                completed.Add(next.Id);
                continue;
            }
            if (completed.Contains(next.Id))
            {
                continue;
            }
            if (!path.Add(next.Id))
            {
                return true;
            }

            pending.Push((next.Id, true));
            var children = next.Id == root ? candidateReferences : references.GetValueOrDefault(next.Id) ?? [];
            foreach (var child in children)
            {
                pending.Push((child, false));
            }
        }

        return false;
    }
}
