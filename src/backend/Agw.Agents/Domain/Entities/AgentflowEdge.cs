using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data;

namespace Agw.Agents.Domain.Entities;

[Table("agentflow_edge")]
public class AgentflowEdge : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public string EdgeId { get; set; } = null!;

    public string SourceNodeId { get; set; } = null!;

    public string TargetNodeId { get; set; } = null!;
    public bool Animated { get; set; } = true;

    public virtual AgentflowNode SourceNode { get; set; } = null!;
    public virtual AgentflowNode TargetNode { get; set; } = null!;
}
