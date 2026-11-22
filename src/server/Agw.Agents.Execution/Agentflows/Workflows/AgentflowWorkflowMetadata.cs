using Agw.Agents.Definitions.Domain.ValueObjects;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Workflows;

internal sealed record AgentflowWorkflowMetadata(
    IReadOnlyDictionary<string, AgentflowHumanGateNode> HumanGateNodes,
    IReadOnlyDictionary<string, CheckpointRequestNode> CheckpointNodes,
    IReadOnlyDictionary<string, string> NodeIdsByExecutorId
);

internal sealed record AgentflowCompilation(Workflow Workflow, IReadOnlyDictionary<string, string> NodeIdsByExecutorId);

internal sealed record AgentflowHumanGateNode(string NodeId, string? Name, HumanGateOptions Options)
{
    public string? NodePath { get; init; }
}

internal sealed record CheckpointRequestNode(string NodeId, string Name);
