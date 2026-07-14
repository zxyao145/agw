using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal sealed class AgentflowBlockBuildContext
{
    /// <summary>
    /// 创建包含 Block 构建所需节点、Agent、会话、跟踪和宿主选项的上下文。
    /// </summary>
    public AgentflowBlockBuildContext(
        Guid agentflowId,
        AgentflowNode blockNode,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AIAgentHostOptions agentHostOptions)
    {
        AgentflowId = agentflowId;
        BlockNode = blockNode;
        NodeMap = nodeMap;
        NodeIdToAgent = nodeIdToAgent;
        SessionScope = sessionScope;
        ExecutionTraceContext = executionTraceContext;
        AgentHostOptions = agentHostOptions;
    }

    public Guid AgentflowId { get; }

    public AgentflowNode BlockNode { get; }

    public IReadOnlyDictionary<string, AgentflowNode> NodeMap { get; }

    public IReadOnlyDictionary<string, AIAgent> NodeIdToAgent { get; }

    public AgentflowAgentSessionScope? SessionScope { get; }

    public AgentflowExecutionTraceContext? ExecutionTraceContext { get; }

    public AIAgentHostOptions AgentHostOptions { get; }
}

internal sealed record AgentflowBlockConfig
{
    public string[]? ParticipantNodeIds { get; init; }

    public string? ManagerNodeId { get; init; }

    public int? MaxRounds { get; init; }

    public int? MaxStalls { get; init; }

    public int? MaxResets { get; init; }

    public bool? RequirePlanSignoff { get; init; }

    public string? HandoffInstructions { get; init; }

    public bool? EnableReturnToPrevious { get; init; }

    public bool? Autonomous { get; init; }

    public int? AutonomousTurnLimit { get; init; }

    public string? ContinuationPrompt { get; init; }
}
