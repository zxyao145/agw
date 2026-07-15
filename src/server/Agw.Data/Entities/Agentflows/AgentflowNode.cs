using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow_node")]
[EntityTypeConfiguration(typeof(AgentflowNodeConfiguration))]
public class AgentflowNode : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public string NodeId { get; set; } = "";

    public AgentflowNodeKind Kind { get; set; }

    public Guid? RelateId { get; set; }

    public string? Name { get; set; }

    public string? PositionJson { get; set; }

    public string? Instructions { get; set; }

    public string? ConfigJson { get; set; }

    public ICollection<AgentflowEdge> SourceEdges { get; set; } = new List<AgentflowEdge>();
    public ICollection<AgentflowEdge> TargetEdges { get; set; } = new List<AgentflowEdge>();
}
