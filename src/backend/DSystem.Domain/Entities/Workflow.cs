using DSystem.Domain.Enums;

namespace DSystem.Domain.Entities;

public class Workflow : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WorkflowOrchestrationPattern Pattern { get; set; }
    public string? ConfigurationJson { get; set; }
    public bool Enable { get; set; } = true;

    public ICollection<WorkflowAgent> Agents { get; set; } = new List<WorkflowAgent>();
}