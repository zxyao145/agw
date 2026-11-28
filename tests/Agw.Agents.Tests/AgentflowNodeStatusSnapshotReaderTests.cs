using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public sealed class AgentflowNodeStatusSnapshotReaderTests
{
    [Fact]
    public void Read_MessagesFromEventLog_ReadsJsonElementProperties()
    {
        var envelope = CreateEnvelope(AgentRuntimeType.Agentflow);
        var interactionId = Guid.CreateVersion7().ToString("D");
        AgwMessage[] messages =
        [
            TurnMessageFactory.CreateStarted(envelope),
            CreateStatus("input", AgwAgentflowNodeStatus.Running),
            new AgwMessage("text", "assistant", AiRole.Assistant, [new AgwTextContent { Content = "text" }]),
            CreateStatus("gate", AgwAgentflowNodeStatus.Waiting, interactionId),
        ];

        var snapshot = AgentflowNodeStatusSnapshotReader.Read(messages);
        var restored = AgentflowNodeStatusSnapshotReader.Read(
            messages.Select(message => JsonUtil.Deserialize<AgwMessage>(JsonUtil.Serialize(message))!)
        );

        Assert.NotNull(restored);
        Assert.NotNull(snapshot);
        Assert.Equal(envelope.TurnId, restored.TurnId);
        Assert.Equal(envelope.AgentId, restored.AgentflowId);
        Assert.Equal(3, restored.AgentflowRevision);
        Assert.Equal(snapshot.Statuses, restored.Statuses);
        Assert.Equal(
            [
                new("input", AgwAgentflowNodeStatus.Running, null),
                new("gate", AgwAgentflowNodeStatus.Waiting, interactionId),
            ],
            restored.Statuses
        );
    }

    [Fact]
    public void Read_AgentTurn_ReturnsNull()
    {
        var start = TurnMessageFactory.CreateStarted(CreateEnvelope(AgentRuntimeType.Agent));

        Assert.Null(AgentflowNodeStatusSnapshotReader.Read([start]));
    }

    [Fact]
    public void Read_FirstMessageIsNotTurnStart_Throws()
    {
        var status = CreateStatus("input", AgwAgentflowNodeStatus.Running);

        var exception = Assert.Throws<AgwException>(() => AgentflowNodeStatusSnapshotReader.Read([status]));

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    [Fact]
    public void Read_AgentflowTurnWithoutRevision_Throws()
    {
        var start = TurnMessageFactory.CreateStarted(CreateEnvelope(AgentRuntimeType.Agentflow));
        start.AdditionalProperties!.Remove(AgwMessageClassifier.AgentflowRevisionKey);

        var exception = Assert.Throws<AgwException>(() => AgentflowNodeStatusSnapshotReader.Read([start]));

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("invalid")]
    public void Read_AgentflowTurnWithInvalidRevision_Throws(object revision)
    {
        var start = TurnMessageFactory.CreateStarted(CreateEnvelope(AgentRuntimeType.Agentflow));
        start.AdditionalProperties![AgwMessageClassifier.AgentflowRevisionKey] = revision;

        var exception = Assert.Throws<AgwException>(() => AgentflowNodeStatusSnapshotReader.Read([start]));

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    private static TurnEnvelope CreateEnvelope(AgentRuntimeType type)
    {
        var turnId = Guid.CreateVersion7();
        return new(
            turnId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            type,
            turnId.ToString("D"),
            type == AgentRuntimeType.Agentflow ? 3 : 0
        );
    }

    private static AgwMessage CreateStatus(string nodeId, string status, string? interactionId = null) =>
        AgentflowMessageMapper.CreateNodeStatusMessage(
            nodeId,
            status,
            interactionId,
            Guid.CreateVersion7().ToString("D")
        );
}
