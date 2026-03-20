using Agw.Domain.Entities;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using MsAgentWorkflowBuilder = Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder;

namespace Agw.Appliaction.Services.Agentflows;

public static class DxAgentWorkflowBuilder
{
    public static Workflow? BuildHandoff(
        IReadOnlyList<AIAgent> orderedAgents,
        IReadOnlyList<AgentflowEdge> edges,
        Dictionary<string, AIAgent> nodeIdToAgent)
    {
        if (orderedAgents.Count == 0)
        {
            return null;
        }

        var startAgent = orderedAgents[0];
        if (orderedAgents.Count == 1)
        {
            return MsAgentWorkflowBuilder.BuildSequential(orderedAgents);
        }

        var builder = MsAgentWorkflowBuilder.CreateHandoffBuilderWith(startAgent);
        var edgesBySource = edges
            .Where(e => nodeIdToAgent.ContainsKey(e.SourceNodeId) && nodeIdToAgent.ContainsKey(e.TargetNodeId))
            .GroupBy(e => e.SourceNodeId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TargetNodeId).ToList());

        foreach (var (sourceNodeId, targetNodeIds) in edgesBySource)
        {
            var sourceAgent = nodeIdToAgent[sourceNodeId];
            var targetAgents = targetNodeIds.Select(id => nodeIdToAgent[id]).ToArray();
            if (targetAgents.Length > 0)
            {
                builder = builder.WithHandoffs(sourceAgent, targetAgents);
            }
        }

        return builder.Build();
    }

    public static Workflow? BuildMagentic(
        IReadOnlyList<AIAgent> agents,
        int maxRounds = 10,
        int maxStallCount = 3,
        int maxResetCount = 2)
    {
        if (agents.Count < 2)
        {
            if (agents.Count == 1)
            {
                return MsAgentWorkflowBuilder.BuildSequential(agents);
            }

            return null;
        }

        return MsAgentWorkflowBuilder.CreateGroupChatBuilderWith(
                allAgents => new MagenticOrchestrationManager(
                    allAgents,
                    maxRounds: maxRounds,
                    maxStallCount: maxStallCount,
                    maxResetCount: maxResetCount))
            .AddParticipants(agents.ToArray())
            .Build();
    }
}
