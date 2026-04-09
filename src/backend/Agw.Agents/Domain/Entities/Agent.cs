using System.ComponentModel.DataAnnotations.Schema;

using Agw.Providers.Domain.Entities;
using Agw.Shared.Data;

namespace Agw.Agents.Domain.Entities;

[Table("agent")]
public class Agent : BaseEntity, IAggregateRoot
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

    public ModelProviderRelation? ModelProvider { get; set; }

    public ICollection<AgentflowNode> Agentflows { get; set; } = new List<AgentflowNode>();
    public ICollection<AgentMcpServerRelation> AgentMcpToolServers { get; set; } = new List<AgentMcpServerRelation>();
    public ICollection<AgentSkillRelation> AgentSkillRelations { get; set; } = new List<AgentSkillRelation>();
}
