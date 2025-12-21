namespace DSystem.Domain.Entities;

public class WorkflowEdge : BaseEntity
{
    public Guid WorkflowId { get; set; }

    public string EdgeId { get; set; } = null!;

    public string SourceNodeId { get; set; } = null!;

    public string TargetNodeId { get; set; } = null!;
    public bool Animated { get; set; } = true;

    public virtual WorkflowNode SourceNode { get; set; } = null!;
    public virtual WorkflowNode TargetNode { get; set; } = null!;


}