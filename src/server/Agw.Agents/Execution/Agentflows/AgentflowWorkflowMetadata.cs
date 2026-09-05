namespace Agw.Agents.Execution.Agentflows;

internal sealed record AgentflowWorkflowMetadata(
    IReadOnlyDictionary<string, AgentflowHumanGateNode> HumanGateNodes,
    IReadOnlyDictionary<string, CheckpointRequestNode> CheckpointNodes
);

internal sealed record AgentflowHumanGateNode(string NodeId, string? Name, string? ConfigJson);

internal sealed record CheckpointRequestNode(string NodeId, string Name);
