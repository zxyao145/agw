namespace DSystem.Domain.Entities;

public class WorkflowAgent : BaseEntity
{
    public Guid WorkflowId { get; set; }
    public Guid AgentId { get; set; }
    public int Order { get; set; }
    public string? Role { get; set; }

    public Workflow? Workflow { get; set; }
    public Agent? Agent { get; set; }
}