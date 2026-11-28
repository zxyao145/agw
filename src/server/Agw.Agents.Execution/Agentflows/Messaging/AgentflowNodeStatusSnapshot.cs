using System.Globalization;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Agentflows.Messaging;

/// <summary>
/// 运行中 Agentflow Turn 的节点状态快照，Statuses 按 turnSequence 排列。
/// The node status snapshot of a running Agentflow turn. Statuses are in turnSequence order.
/// </summary>
public sealed record AgentflowNodeStatusSnapshot
{
    public AgentflowNodeStatusSnapshot(
        Guid turnId,
        Guid agentflowId,
        int agentflowRevision,
        IReadOnlyList<AgentflowNodeStatusEntry> statuses
    )
    {
        TurnId = turnId;
        AgentflowId = agentflowId;
        AgentflowRevision = agentflowRevision;
        Statuses = statuses;
    }

    public Guid TurnId { get; }
    public Guid AgentflowId { get; }
    public int AgentflowRevision { get; }
    public IReadOnlyList<AgentflowNodeStatusEntry> Statuses { get; }
}

public sealed record AgentflowNodeStatusEntry(string NodeId, string Status, string? InteractionId);

internal static class AgentflowNodeStatusSnapshotReader
{
    public static AgentflowNodeStatusSnapshot? Read(IEnumerable<AgwMessage> messages)
    {
        using var entries = messages.GetEnumerator();
        if (!entries.MoveNext() || !AgwMessageClassifier.IsTurnStart(entries.Current))
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "Node status snapshot is missing the turn start.");

        var start = entries.Current;
        var agentType = ReadProperty(start, AgwMessageClassifier.AgentTypeKey);
        if (agentType == AgwMessageClassifier.AgentTypeValue)
            return null;
        if (
            agentType != AgwMessageClassifier.AgentflowTypeValue
            || !Guid.TryParse(ReadProperty(start, AgwMessageClassifier.TurnIdKey), out var turnId)
            || turnId == Guid.Empty
            || !Guid.TryParse(ReadProperty(start, AgwMessageClassifier.AgentIdKey), out var agentflowId)
            || agentflowId == Guid.Empty
        )
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Node status snapshot has an invalid turn identity."
            );
        var revision = ReadRevision(start);
        var statuses = new List<AgentflowNodeStatusEntry>();
        while (entries.MoveNext())
        {
            var message = entries.Current;
            if (AgwMessageClassifier.GetMessageType(message) == AgwMessageTypes.AgentflowNodeStatus)
                statuses.Add(ReadStatus(message));
        }
        return new AgentflowNodeStatusSnapshot(turnId, agentflowId, revision, statuses);
    }

    private static int ReadRevision(AgwMessage start)
    {
        if (
            !int.TryParse(
                ReadProperty(start, AgwMessageClassifier.AgentflowRevisionKey),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var revision
            )
            || revision <= 0
        )
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Node status snapshot has an invalid Agentflow revision."
            );

        return revision;
    }

    private static AgentflowNodeStatusEntry ReadStatus(AgwMessage message)
    {
        var nodeId = ReadProperty(message, AgwMessageClassifier.NodeIdKey);
        var status = ReadProperty(message, AgwMessageClassifier.StatusKey);
        if (
            string.IsNullOrWhiteSpace(nodeId)
            || status
                is not (
                    AgwAgentflowNodeStatus.Running
                    or AgwAgentflowNodeStatus.Waiting
                    or AgwAgentflowNodeStatus.Completed
                    or AgwAgentflowNodeStatus.Failed
                )
        )
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Node status snapshot contains an invalid node status."
            );

        return new AgentflowNodeStatusEntry(
            nodeId,
            status,
            ReadProperty(message, AgwMessageClassifier.InteractionIdKey)
        );
    }

    private static string? ReadProperty(AgwMessage message, string key) =>
        message.AdditionalProperties?.TryGetValue(key, out var value) == true ? value?.ToString() : null;
}
