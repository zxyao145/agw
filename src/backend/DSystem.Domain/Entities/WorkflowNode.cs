using DSystem.Domain.Enums;
using Microsoft.Agents.AI.Workflows;
using System.Xml.Linq;

namespace DSystem.Domain.Entities;

public class WorkflowNode : BaseEntity
{
    public Guid WorkflowId { get; set; }

    public string NodeId { get; set; } = "";

    public WorkflowNodeType Type { get; set; }

    public Guid RelateId { get; set; }

    

    public ICollection<WorkflowEdge> SourceEdges { get; set; } = new List<WorkflowEdge>();
    public ICollection<WorkflowEdge> TargetEdges { get; set; } = new List<WorkflowEdge>();
}
