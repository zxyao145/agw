using System.Text.Json;

using Agw.Shared.Data.Entities.Agentflows;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal static class AgentflowBlockBuildSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 读取 Block 配置；配置为空或 JSON 无效时返回默认配置。
    /// </summary>
    internal static AgentflowBlockConfig ReadConfig(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return new AgentflowBlockConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AgentflowBlockConfig>(node.ConfigJson, JsonOptions) ??
                   new AgentflowBlockConfig();
        }
        catch (JsonException)
        {
            return new AgentflowBlockConfig();
        }
    }

    /// <summary>
    /// 按配置顺序解析并包装 Block 参与者；任一参与者无效时返回空结果。
    /// </summary>
    internal static IReadOnlyList<(string NodeId, AIAgent Agent)>? ResolveParticipants(
        AgentflowBlockBuildContext context)
    {
        var config = ReadConfig(context.BlockNode);
        var participantNodeIds = config.ParticipantNodeIds?
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (participantNodeIds.Count == 0)
        {
            return null;
        }

        var participants = new List<(string NodeId, AIAgent Agent)>();
        foreach (var participantNodeId in participantNodeIds)
        {
            var participant = CreateParticipant(
                context,
                participantNodeId,
                $"{context.BlockNode.NodeId}.{participantNodeId}");
            if (participant == null)
            {
                return null;
            }

            participants.Add((participantNodeId, participant));
        }

        return participants;
    }

    /// <summary>
    /// 为指定参与节点创建带节点指令、会话作用域和可选执行跟踪的运行时 Agent。
    /// </summary>
    internal static AIAgent? CreateParticipant(
        AgentflowBlockBuildContext context,
        string participantNodeId,
        string runtimeNodeId)
    {
        if (!context.NodeMap.TryGetValue(participantNodeId, out var participantNode) ||
            !context.NodeIdToAgent.TryGetValue(participantNodeId, out var participantAgent))
        {
            return null;
        }

        var shouldTrace = participantNode.Kind == AgentflowNodeKind.Agent;
        return new AgentflowNodeScopedAgent(
            participantAgent,
            runtimeNodeId,
            participantNode.Name,
            participantNode.Instructions,
            context.SessionScope,
            shouldTrace ? context.ExecutionTraceContext : null,
            context.AgentflowId,
            shouldTrace ? participantNode.NodeId : null,
            shouldTrace ? participantNode.RelateId : null,
            historyNodeId: participantNode.NodeId);
    }

    /// <summary>
    /// 将内部 Workflow 包装为 Block Agent，并绑定为使用统一宿主选项的执行器。
    /// </summary>
    internal static ExecutorBinding BindWorkflow(
        AgentflowBlockBuildContext context,
        Workflow workflow)
    {
        var blockNode = context.BlockNode;
        var blockAgent = workflow.AsAIAgent(
            id: blockNode.NodeId,
            name: blockNode.Name ?? blockNode.NodeId,
            description: string.Empty,
            includeWorkflowOutputsInResponse: true);

        return new AgentflowNodeScopedAgent(
                blockAgent,
                blockNode.NodeId,
                blockNode.Name,
                blockNode.Instructions,
                context.SessionScope,
                agentflowId: context.AgentflowId)
            .BindAsExecutor(context.AgentHostOptions);
    }
}
