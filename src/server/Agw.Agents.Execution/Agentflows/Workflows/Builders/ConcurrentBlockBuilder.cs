using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Workflows.Builders;

internal static class ConcurrentBlockBuilder
{
    /// <summary>
    /// 构建并发执行全部参与者并汇总其响应消息的 Block 执行器。
    /// </summary>
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        var workflow = AgentWorkflowBuilder.BuildConcurrent(
            participants.Select(participant => participant.Agent),
            responses => responses.SelectMany(messages => messages).ToList()
        );
        return AgentflowBlockBuildSupport.BindWorkflow(context, workflow);
    }
}
