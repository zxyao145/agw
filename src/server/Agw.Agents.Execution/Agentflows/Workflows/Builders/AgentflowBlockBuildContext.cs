using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Workflows.Builders;

internal sealed class AgentflowBlockBuildContext
{
    /// <summary>
    /// 创建包含 Block 构建所需节点、Agent、会话、跟踪和宿主选项的上下文。
    /// </summary>
    public AgentflowBlockBuildContext(
        Guid agentflowId,
        int agentflowRevision,
        AgentflowNode blockNode,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        IReadOnlyDictionary<string, EngineKind> nodeEngineKinds,
        IReadOnlyDictionary<string, AgentflowNodeResultOptions> nodeResultOptions,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AgentflowSummaryContext? summaryContext,
        AIAgentHostOptions agentHostOptions
    )
    {
        AgentflowId = agentflowId;
        AgentflowRevision = agentflowRevision;
        BlockNode = blockNode;
        Members = members;
        NodeIdToAgent = nodeIdToAgent;
        NodeEngineKinds = nodeEngineKinds;
        NodeResultOptions = nodeResultOptions;
        SessionScope = sessionScope;
        ExecutionTraceContext = executionTraceContext;
        SummaryContext = summaryContext;
        AgentHostOptions = agentHostOptions;
    }

    public Guid AgentflowId { get; }

    public int AgentflowRevision { get; }

    public AgentflowNode BlockNode { get; }

    public IReadOnlyList<AgentflowBlockMember> Members { get; }

    public IReadOnlyDictionary<string, AIAgent> NodeIdToAgent { get; }

    public IReadOnlyDictionary<string, EngineKind> NodeEngineKinds { get; }

    public IReadOnlyDictionary<string, AgentflowNodeResultOptions> NodeResultOptions { get; }

    public AgentflowAgentSessionScope? SessionScope { get; }

    public AgentflowExecutionTraceContext? ExecutionTraceContext { get; }

    public AgentflowSummaryContext? SummaryContext { get; }

    public AIAgentHostOptions AgentHostOptions { get; }
}
