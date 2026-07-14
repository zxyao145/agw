using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal static class GroupChatBlockBuilder
{
    /// <summary>
    /// 构建按轮询顺序协作并受最大轮次限制的 GroupChat Block 执行器。
    /// </summary>
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        var config = AgentflowBlockBuildSupport.ReadConfig(context.BlockNode);
        var maxRounds = Math.Max(1, config.MaxRounds ?? 10);
        var workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
            {
                var manager = new RoundRobinGroupChatManager(
                    agents,
                    (roundRobinManager, _, _) =>
                        new ValueTask<bool>(roundRobinManager.IterationCount >= maxRounds));
                manager.MaximumIterationCount = maxRounds;
                return manager;
            })
            .AddParticipants(participants.Select(participant => participant.Agent))
            .Build();

        return AgentflowBlockBuildSupport.BindWorkflow(context, workflow);
    }
}
