using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal static class MagenticBlockBuilder
{
    /// <summary>
    /// 构建由指定管理者协调团队并应用轮次、停滞和重置限制的 Magentic Block 执行器。
    /// </summary>
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        var config = AgentflowBlockBuildSupport.ReadConfig(context.BlockNode);
        var managerNodeId = participants[0].NodeId;
        var manager = participants[0].Agent;
        var team = participants.Skip(1).Select(participant => participant.Agent).ToList();
        if (!string.IsNullOrWhiteSpace(config.ManagerNodeId))
        {
            var configuredManager = AgentflowBlockBuildSupport.CreateParticipant(
                context,
                config.ManagerNodeId,
                $"{context.BlockNode.NodeId}.{config.ManagerNodeId}.manager"
            );
            if (configuredManager == null)
            {
                return null;
            }

            manager = configuredManager;
            managerNodeId = config.ManagerNodeId;
            team = participants
                .Where(participant => !string.Equals(participant.NodeId, managerNodeId, StringComparison.Ordinal))
                .Select(participant => participant.Agent)
                .ToList();
        }

        var builder = AgentWorkflowBuilder.CreateMagenticBuilderWith(manager).AddParticipants(team);
        if (config.MaxRounds.HasValue)
        {
            builder = builder.WithMaxRounds(config.MaxRounds);
        }

        if (config.MaxStalls.HasValue)
        {
            builder = builder.WithMaxStalls(config.MaxStalls.Value);
        }

        if (config.MaxResets.HasValue)
        {
            builder = builder.WithMaxResets(config.MaxResets);
        }

        if (config.RequirePlanSignoff.HasValue)
        {
            builder = builder.RequirePlanSignoff(config.RequirePlanSignoff.Value);
        }

        return AgentflowBlockBuildSupport.BindWorkflow(context, builder.Build());
    }
}
