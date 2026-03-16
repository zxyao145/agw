namespace DSystem.Shared.Enums;

/// <summary>
/// Agentflow orchestration patterns.
/// </summary>
public enum AgentflowOrchestrationPattern
{
    /// <summary>
    /// Broadcasts a task to all agents and collects results independently.
    /// </summary>
    Concurrent = 0,

    /// <summary>
    /// Passes the result from one agent to the next in a defined order.
    /// </summary>
    Sequential = 1,

    /// <summary>
    /// Collaborative conversation with a manager controlling speaker selection and flow.
    /// </summary>
    GroupChat = 2,

    /// <summary>
    /// Dynamically passes control between agents based on context or rules.
    /// </summary>
    Handoff = 3,

    /// <summary>
    /// Inspired by MagenticOne - a generalist multi-agent collaboration pattern.
    /// </summary>
    Magentic = 4
}