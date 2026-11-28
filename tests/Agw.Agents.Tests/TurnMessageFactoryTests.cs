using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Tests;

public sealed class TurnMessageFactoryTests
{
    [Fact]
    public void CreateStarted_AgentflowEnvelope_IncludesAgentflowRevision()
    {
        var turnId = Guid.CreateVersion7();
        var envelope = new TurnEnvelope(
            turnId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AgentRuntimeType.Agentflow,
            turnId.ToString("D"),
            3
        );

        var start = TurnMessageFactory.CreateStarted(envelope);
        var finish = TurnMessageFactory.CreateFinished(envelope, AgwTurnStatus.Completed, 1, null);

        Assert.Equal(3, start.AdditionalProperties![AgwMessageClassifier.AgentflowRevisionKey]);
        Assert.Equal(3, finish.AdditionalProperties![AgwMessageClassifier.AgentflowRevisionKey]);
    }

    [Fact]
    public void CreateStarted_AgentEnvelope_OmitsAgentflowRevision()
    {
        var turnId = Guid.CreateVersion7();
        var envelope = new TurnEnvelope(
            turnId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AgentRuntimeType.Agent,
            turnId.ToString("D"),
            0
        );

        var start = TurnMessageFactory.CreateStarted(envelope);

        Assert.DoesNotContain(AgwMessageClassifier.AgentflowRevisionKey, start.AdditionalProperties!.Keys);
    }
}
