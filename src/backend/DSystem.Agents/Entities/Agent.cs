using DSystem.Shared;
using DSystem.Shared.Enums;

namespace DSystem.Domain.Entities;

public class Agent : BaseEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// System prompt / instructions for the agent's LLM.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Model Provider ID. Required for System agents, optional for External agents.
    /// </summary>
    public Guid? ModelProviderId { get; set; }

    public string? Tools { get; set; }  // JSON array of tool method names
    public AgentType Type { get; set; } = AgentType.System;

    /// <summary>
    /// JSON object for additional data (e.g., environment variables).
    /// </summary>
    public string? Extra { get; set; }

    public ModelProvider? ModelProvider { get; set; }

    public ICollection<AgentflowNode> Agentflows { get; set; } = new List<AgentflowNode>();
    public ICollection<AgentMcpToolServer> AgentMcpToolServers { get; set; } = new List<AgentMcpToolServer>();
}
