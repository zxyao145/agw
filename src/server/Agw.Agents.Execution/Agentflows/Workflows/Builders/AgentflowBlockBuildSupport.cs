using Agw.Agents.Execution.Agentflows.Context;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Workflows.Builders;

internal static class AgentflowBlockBuildSupport
{
    /// <summary>
    /// 按成员顺序解析并包装 Block 参与者；任一参与者无效时返回空结果。
    /// </summary>
    internal static IReadOnlyList<(AgentflowBlockMember Member, AIAgent Agent)>? ResolveParticipants(
        AgentflowBlockBuildContext context
    )
    {
        var members = context.Members.OrderBy(member => member.SortOrder).ToList();
        if (members.Count == 0)
        {
            return null;
        }

        var participants = new List<(AgentflowBlockMember Member, AIAgent Agent)>();
        foreach (var member in members)
        {
            var participant = CreateParticipant(
                context,
                member,
                AgentflowBlockMemberKey.Create(member.NodeId, member.MemberId)
            );
            if (participant == null)
            {
                return null;
            }

            participants.Add((member, participant));
        }

        return participants;
    }

    /// <summary>
    /// 为指定参与节点创建带节点指令、会话作用域和可选执行跟踪的运行时 Agent。
    /// </summary>
    internal static AIAgent? CreateParticipant(
        AgentflowBlockBuildContext context,
        AgentflowBlockMember member,
        string runtimeNodeId
    )
    {
        var memberKey = AgentflowBlockMemberKey.Create(member.NodeId, member.MemberId);
        if (!context.NodeIdToAgent.TryGetValue(memberKey, out var participantAgent))
        {
            return null;
        }

        var shouldTrace = member.Kind == AgentflowNodeKind.Agent;
        return new AgentflowNodeScopedAgent(
            participantAgent,
            runtimeNodeId,
            member.Name,
            member.Instructions,
            context.SessionScope,
            shouldTrace ? context.ExecutionTraceContext : null,
            context.AgentflowId,
            context.AgentflowRevision,
            traceNodeId: shouldTrace ? member.NodeId : null,
            agentId: shouldTrace ? member.RelateId : null,
            historyNodeId: runtimeNodeId,
            isWorkflow: member.Kind == AgentflowNodeKind.WorkflowAsAgent,
            engineKind: context.NodeEngineKinds.TryGetValue(memberKey, out var engineKind) ? engineKind : null,
            resultOptions: context.NodeResultOptions.GetValueOrDefault(memberKey),
            summaryContext: context.SummaryContext
        );
    }

    /// <summary>
    /// 将内部 Workflow 包装为 Block Agent，并绑定为使用统一宿主选项的执行器。
    /// </summary>
    internal static ExecutorBinding BindWorkflow(AgentflowBlockBuildContext context, Workflow workflow)
    {
        var blockNode = context.BlockNode;
        // 2026-10-13: Block 的内部 Workflow 随当前枚举推进，按顺序处理暂停和恢复。
        // Advance a Block's inner workflow through the current enumeration to order pause and resume.
        var blockAgent = workflow.AsAIAgent(
            id: blockNode.NodeId,
            name: blockNode.Name ?? blockNode.NodeId,
            description: string.Empty,
            executionEnvironment: InProcessExecution.Lockstep,
            includeWorkflowOutputsInResponse: true
        );

        return new AgentflowNodeScopedAgent(
            blockAgent,
            blockNode.NodeId,
            blockNode.Name,
            blockNode.Instructions,
            context.SessionScope,
            agentflowId: context.AgentflowId,
            agentflowRevision: context.AgentflowRevision,
            isWorkflow: true
        ).BindAsExecutor(context.AgentHostOptions);
    }
}
