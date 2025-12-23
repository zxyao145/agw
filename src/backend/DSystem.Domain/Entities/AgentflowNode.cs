using DSystem.Domain.Enums;

namespace DSystem.Domain.Entities;

public class AgentflowNode : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public string NodeId { get; set; } = "";

    public AgentflowNodeType Type { get; set; }

    public Guid RelateId { get; set; }



    public ICollection<AgentflowEdge> SourceEdges { get; set; } = new List<AgentflowEdge>();
    public ICollection<AgentflowEdge> TargetEdges { get; set; } = new List<AgentflowEdge>();
}
