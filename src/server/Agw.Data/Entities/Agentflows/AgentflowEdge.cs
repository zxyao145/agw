using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow_edge")]
[EntityTypeConfiguration(typeof(AgentflowEdgeConfiguration))]
public class AgentflowEdge : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public int AgentflowRevision { get; set; }

    public string EdgeId { get; set; } = null!;

    public string SourceNodeId { get; set; } = null!;

    public string TargetNodeId { get; set; } = null!;

    public string? Label { get; set; }

    public string ConditionJson { get; set; } = "[]";

    public bool IsFanInBarrier { get; set; }

    public bool IsSwitchDefault { get; set; }

    public int SwitchCaseOrder { get; set; }

    public string? ChoiceId { get; set; }

    public virtual AgentflowNode SourceNode { get; set; } = null!;
    public virtual AgentflowNode TargetNode { get; set; } = null!;
}
