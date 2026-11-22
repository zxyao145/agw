using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Projects;

public sealed record ProjectConversationTurnExtra
{
    public int AgentflowRevision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectConversationTurnAgentflowDependency>? AgentflowDependencies { get; init; }
}

public sealed record ProjectConversationTurnAgentflowDependency
{
    public ProjectConversationTurnAgentflowDependency(
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
