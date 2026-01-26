using DSystem.Domain.Entities;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using MsAgentWorkflowBuilder = Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder;

namespace DSystem.Domain.Services;

/// <summary>
/// Extension methods for <see cref="MsAgentWorkflowBuilder"/> to support additional orchestration patterns.
/// </summary>
public static class DxAgentWorkflowBuilder
{
    /// <summary>
    /// Builds a handoff workflow based on edge connections.
    /// </summary>
    /// <remarks>
    /// The handoff pattern allows agents to dynamically route conversations to other agents
    /// based on context. The first agent in the ordered list serves as the triage/entry point.
    /// Edges define the allowed handoff routes between agents.
    ///
    /// Example topology:
    /// <code>
    ///     TriageAgent
    ///        /    \
    ///   MathAgent  HistoryAgent
    ///        \    /
    ///     TriageAgent (return)
    /// </code>
    /// </remarks>
    /// <param name="orderedAgents">Agents ordered by topological sort (first is start agent)</param>
    /// <param name="edges">Edge definitions specifying allowed handoff routes</param>
    /// <param name="nodeIdToAgent">Mapping from node IDs to their corresponding AIAgent instances</param>
    /// <returns>A configured handoff workflow, or null if no agents provided</returns>
    public static Workflow? BuildHandoff(
        IReadOnlyList<AIAgent> orderedAgents,
        IReadOnlyList<AgentflowEdge> edges,
        Dictionary<string, AIAgent> nodeIdToAgent)
    {
        if (orderedAgents.Count == 0)
        {
            return null;
        }

        // The first agent in the ordered list is the starting/triage agent
        var startAgent = orderedAgents[0];

        if (orderedAgents.Count == 1)
        {
            // Single agent - fall back to sequential workflow
            return MsAgentWorkflowBuilder.BuildSequential(orderedAgents);
        }

        // Initialize the handoff workflow with the starting agent
        var builder = MsAgentWorkflowBuilder.CreateHandoffBuilderWith(startAgent);

        // Group edges by source node to build handoff relationships
        // Each source agent can hand off to multiple target agents
        var edgesBySource = edges
            .Where(e => nodeIdToAgent.ContainsKey(e.SourceNodeId) && nodeIdToAgent.ContainsKey(e.TargetNodeId))
            .GroupBy(e => e.SourceNodeId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TargetNodeId).ToList());

        foreach (var (sourceNodeId, targetNodeIds) in edgesBySource)
        {
            var sourceAgent = nodeIdToAgent[sourceNodeId];
            var targetAgents = targetNodeIds
                .Select(id => nodeIdToAgent[id])
                .ToArray();

            if (targetAgents.Length > 0)
            {
                // Configure which agents this source agent can hand off to
                builder = builder.WithHandoffs(sourceAgent, targetAgents);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a Magentic-One style workflow with an orchestrator and worker agents.
    /// </summary>
    /// <remarks>
    /// The Magentic-One pattern implements a hierarchical multi-agent system where:
    /// - The first agent acts as the orchestrator (coordinator)
    /// - Remaining agents are workers that execute tasks assigned by the orchestrator
    /// - The orchestrator manages task distribution, monitors progress, and detects stalls
    ///
    /// Configuration parameters:
    /// - maxRounds: Maximum collaboration rounds before forced termination
    /// - maxStallCount: Consecutive rounds without progress before orchestrator intervention
    /// - maxResetCount: Maximum plan resets allowed before termination
    /// </remarks>
    /// <param name="agents">Agents where first is orchestrator, rest are workers</param>
    /// <param name="maxRounds">Maximum collaboration rounds (default: 10)</param>
    /// <param name="maxStallCount">Rounds without progress before intervention (default: 3)</param>
    /// <param name="maxResetCount">Maximum plan resets allowed (default: 2)</param>
    /// <returns>A configured Magentic workflow, or null if insufficient agents</returns>
    public static Workflow? BuildMagentic(
        IReadOnlyList<AIAgent> agents,
        int maxRounds = 10,
        int maxStallCount = 3,
        int maxResetCount = 2)
    {
        if (agents.Count < 2)
        {
            // Magentic requires at least 2 agents: orchestrator + at least one worker
            // Fall back to sequential for single agent
            if (agents.Count == 1)
            {
                return MsAgentWorkflowBuilder.BuildSequential(agents);
            }
            return null;
        }

        var workflow = MsAgentWorkflowBuilder.CreateGroupChatBuilderWith(
            allAgents => new MagenticOrchestrationManager(
                allAgents,
                maxRounds: maxRounds,
                maxStallCount: maxStallCount,
                maxResetCount: maxResetCount
            ))
            .AddParticipants(agents.ToArray())
            .Build();

        return workflow;
    }
}
