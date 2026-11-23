namespace Agw.Projects.Contracts.History;

public sealed record AgentflowDependencyRevisionDto
{
    public AgentflowDependencyRevisionDto(
        Guid parentAgentflowId,
        int parentRevision,
        string parentNodeId,
        string? blockMemberId,
        Guid targetAgentflowId,
        int targetRevision
    )
    {
        ParentAgentflowId = parentAgentflowId;
        ParentRevision = parentRevision;
        ParentNodeId = parentNodeId;
        BlockMemberId = blockMemberId;
        TargetAgentflowId = targetAgentflowId;
        TargetRevision = targetRevision;
    }

    public Guid ParentAgentflowId { get; }
    public int ParentRevision { get; }
    public string ParentNodeId { get; }
    public string? BlockMemberId { get; }
    public Guid TargetAgentflowId { get; }
    public int TargetRevision { get; }
}
