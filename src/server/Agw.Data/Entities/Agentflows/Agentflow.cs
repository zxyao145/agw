using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow")]
[EntityTypeConfiguration(typeof(AgentflowConfiguration))]
public class Agentflow : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public Guid? SummaryModelProviderId { get; set; }

    public ICollection<AgentflowNode> Nodes { get; set; } = new List<AgentflowNode>();
    public ICollection<AgentflowEdge> Edges { get; set; } = new List<AgentflowEdge>();
}
