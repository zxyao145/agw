using Agw.Agents.Definitions.Domain.Topology;

namespace Agw.Agents.Tests;

public class AgentflowReferenceTopologyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HasCycle_ReferenceReturnsToAncestor_Rejects(int length)
    {
        var ids = Enumerable.Range(0, length).Select(_ => Guid.NewGuid()).ToArray();
        var references = ids.Select((id, index) => (id, next: ids[(index + 1) % length]))
            .ToDictionary(item => item.id, item => (IReadOnlyCollection<Guid>)[item.next]);

        Assert.True(AgentflowReferenceTopology.HasCycle(ids[0], references[ids[0]], references));
    }

    [Fact]
    public void HasCycle_SharedDescendant_IsAllowed()
    {
        var root = Guid.NewGuid();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var references = new Dictionary<Guid, IReadOnlyCollection<Guid>>
        {
            [root] = [root], // The candidate replaces the persisted edges.
            [left] = [shared],
            [right] = [shared],
            [shared] = [],
        };

        Assert.False(AgentflowReferenceTopology.HasCycle(root, [left, right], references));
    }
}
