using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;

namespace Agw.Agents.Execution.Agentflows.Checkpoints;

internal sealed record AgentflowCheckpointMarker(string NodeId, string Name, string MessageId);

internal sealed record AgentflowCheckpointSnapshot(
    Guid OccurrenceId,
    Guid? SourceTurnId,
    Guid AgentflowId,
    long BoundarySequence,
    int AgentflowRevision,
    int RuntimeVersion,
    IReadOnlyList<AgentflowCheckpointMarker> Markers,
    AgentflowRevisionSnapshot RevisionSnapshot,
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
