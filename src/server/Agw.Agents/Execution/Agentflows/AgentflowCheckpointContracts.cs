using Agw.Agents.Execution.Durable;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Agentflows;

internal sealed record AgentflowCheckpointMarker(string NodeId, string Name, string MessageId);

internal sealed record AgentflowCheckpointSnapshot(
    Guid OccurrenceId,
    Guid? SourceExecutionId,
    Guid AgentflowId,
    long BoundarySequence,
    string DefinitionFingerprint,
    IReadOnlyList<AgentflowCheckpointMarker> Markers,
    DurableAgentflowCheckpoint Checkpoint
);

internal sealed record RecordedAgentflowCheckpoint(
    AgentflowCheckpointSnapshot Snapshot,
    IReadOnlyList<AgwMessage> Messages
);

public sealed record AgentflowCheckpointAvailability(
    Guid OccurrenceId,
    Guid AgentflowId,
    long BoundarySequence,
    bool Available,
    IReadOnlyList<AgentflowCheckpointMarkerInfo> Markers
);

public sealed record AgentflowCheckpointMarkerInfo(string NodeId, string Name, string MessageId);
