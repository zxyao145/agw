using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Workflows.Builders;

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

        var config = AgentflowDefinitionJson.ReadOptions<MagenticBlockOptions>(context.BlockNode);
        var manager = participants.Single(participant => participant.Member.IsManager).Agent;
        var team = participants
            .Where(participant => !participant.Member.IsManager)
            .Select(participant => participant.Agent);

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
