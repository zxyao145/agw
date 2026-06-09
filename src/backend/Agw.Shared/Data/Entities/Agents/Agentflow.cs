using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Contracts.Agents;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agentflow")]
public class Agentflow : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public AgentflowOrchestrationPattern Pattern { get; set; }
    public string? ConfigurationJson { get; set; }
    public bool Enable { get; set; } = true;

    public ICollection<AgentflowNode> Nodes { get; set; } = new List<AgentflowNode>();
    public ICollection<AgentflowEdge> Edges { get; set; } = new List<AgentflowEdge>();
}
