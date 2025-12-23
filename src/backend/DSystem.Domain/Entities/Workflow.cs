using DSystem.Domain.Enums;

namespace DSystem.Domain.Entities;

public class Workflow : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public WorkflowOrchestrationPattern Pattern { get; set; }
    public string? ConfigurationJson { get; set; }
    public bool Enable { get; set; } = true;

    public ICollection<WorkflowNode> Nodes { get; set; } = new List<WorkflowNode>();
    public ICollection<WorkflowEdge> Edges { get; set; } = new List<WorkflowEdge>();
}