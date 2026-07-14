using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow_edge")]
public class AgentflowEdge : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public string EdgeId { get; set; } = null!;

    public string SourceNodeId { get; set; } = null!;

    public string TargetNodeId { get; set; } = null!;

    public AgentflowEdgeKind Kind { get; set; } = AgentflowEdgeKind.Direct;

    public string? Label { get; set; }

    public string? ConditionJson { get; set; }

    public string? ConfigJson { get; set; }

    public virtual AgentflowNode SourceNode { get; set; } = null!;
    public virtual AgentflowNode TargetNode { get; set; } = null!;
}
