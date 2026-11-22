using Agw.Projects.Contracts.History;

namespace Agw.Agents.Application.Persistence;

public static class AgentflowDependencyRevisionMapping
{
    public static AgentflowDependencyRevision FromDto(AgentflowDependencyRevisionDto dependency) =>
        new(
            dependency.ParentAgentflowId,
            dependency.ParentRevision,
            dependency.ParentNodeId,
            dependency.BlockMemberId,
            dependency.TargetAgentflowId,
            dependency.TargetRevision
        );

    public static AgentflowDependencyRevisionDto ToDto(AgentflowDependencyRevision dependency) =>
        new(
            dependency.ParentAgentflowId,
            dependency.ParentRevision,
            dependency.ParentNodeId,
            dependency.BlockMemberId,
            dependency.TargetAgentflowId,
            dependency.TargetRevision
        );
}
