using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Tooling;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent")]
[EntityTypeConfiguration(typeof(AgentConfiguration))]
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

    public bool EnableSummary { get; set; }

    public Guid? SummaryModelProviderId { get; set; }

    public AgentType Type { get; set; } = AgentType.System;

    /// <summary>
    /// JSON object for additional external agent settings.
    /// </summary>
    public string? Extra { get; set; }

    public List<ToolValueObject> Tools { get; set; } = [];

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    public ModelProviderRelation? ModelProvider { get; set; }

    public ICollection<AgentSkillRelation> AgentSkillRelations { get; set; } = new List<AgentSkillRelation>();

    public ICollection<AgentMcpServerRelation> AgentMcpToolServers { get; set; } = new List<AgentMcpServerRelation>();

    public ICollection<AgentConnectionRelation> AgentConnectionRelations { get; set; } =
        new List<AgentConnectionRelation>();
}
