using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public sealed class AgentflowBehaviorTests
{
    [Fact]
    public void StartRevisions_NewAgentflow_StartsAtOneAndIsUnpublished()
    {
        var agentflow = new Agentflow();

        new AgentflowBehavior(agentflow).StartRevisions();

        Assert.Equal(1, agentflow.LatestRevision);
        Assert.Equal(0, agentflow.PublishedRevision);
    }

    [Fact]
    public void TryAdvanceRevision_BaseRevisionIsLatest_IncrementsLatest()
    {
        var agentflow = new Agentflow { LatestRevision = 3, PublishedRevision = 2 };

        var advanced = new AgentflowBehavior(agentflow).TryAdvanceRevision(3);

        Assert.True(advanced);
        Assert.Equal(4, agentflow.LatestRevision);
        Assert.Equal(2, agentflow.PublishedRevision);
    }

    [Fact]
    public void TryAdvanceRevision_BaseRevisionIsStale_LeavesLatestUnchanged()
    {
        var agentflow = new Agentflow { LatestRevision = 3, PublishedRevision = 2 };

        var advanced = new AgentflowBehavior(agentflow).TryAdvanceRevision(2);

        Assert.False(advanced);
        Assert.Equal(3, agentflow.LatestRevision);
    }

    [Fact]
    public void Publish_Revision_SetsPublishedPointer()
    {
        var agentflow = new Agentflow { LatestRevision = 3, PublishedRevision = 1 };

        new AgentflowBehavior(agentflow).Publish(3);

        Assert.Equal(3, agentflow.PublishedRevision);
    }
}
