using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal static class HandoffBlockBuilder
{
    /// <summary>
    /// 构建支持交接指令、返回上一参与者和自治模式的 Handoff Block 执行器。
    /// </summary>
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        var participantAgents = participants.Select(participant => participant.Agent).ToList();
        var config = AgentflowBlockBuildSupport.ReadConfig(context.BlockNode);
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(participantAgents[0])
            .AddParticipants(participantAgents.Skip(1));
        if (!string.IsNullOrWhiteSpace(config.HandoffInstructions))
        {
            builder = builder.WithHandoffInstructions(config.HandoffInstructions);
        }

        if (config.EnableReturnToPrevious == true)
        {
            builder = builder.EnableReturnToPrevious();
        }

        if (config.Autonomous == true)
        {
            builder = builder.WithAutonomousMode(
                config.AutonomousTurnLimit,
                config.ContinuationPrompt,
                participantAgents,
                null!,
                null!);
        }

        return AgentflowBlockBuildSupport.BindWorkflow(context, builder.Build());
    }
}
