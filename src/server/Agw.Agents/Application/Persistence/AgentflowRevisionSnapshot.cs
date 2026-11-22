using System.Text.Json.Serialization;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Application.Persistence;

public readonly record struct AgentflowDependencyRevision
{
    [JsonConstructor]
    public AgentflowDependencyRevision(
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

public sealed record AgentflowRevisionSnapshot(
    int RootRevision,
    IReadOnlyList<AgentflowDependencyRevision> Dependencies
)
{
    public static AgentflowRevisionSnapshot FromTurn(ConversationTurnSnapshot turn)
    {
        if (turn.TargetType != ConversationTurnTargetType.Agentflow || turn.AgentflowDependencies is null)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }

        return new AgentflowRevisionSnapshot(
            turn.AgentflowRevision,
            turn.AgentflowDependencies.Select(AgentflowDependencyRevisionMapping.FromDto).ToArray()
        );
    }

    public bool PinsDefinition(Guid rootAgentflowId, Guid agentflowId, int revision) =>
        (rootAgentflowId == agentflowId && RootRevision == revision)
        || Dependencies.Any(dependency =>
            dependency.TargetAgentflowId == agentflowId && dependency.TargetRevision == revision
        );

    public bool MatchesDependencies(IReadOnlyList<AgentflowDependencyRevision>? dependencies) =>
        dependencies is not null && OrderDependencies(Dependencies).SequenceEqual(OrderDependencies(dependencies));

    public IReadOnlySet<(Guid AgentflowId, int Revision)> GetDefinitions(Guid rootAgentflowId)
    {
        var definitions = new HashSet<(Guid AgentflowId, int Revision)> { (rootAgentflowId, RootRevision) };
        foreach (var dependency in Dependencies)
        {
            definitions.Add((dependency.TargetAgentflowId, dependency.TargetRevision));
        }

        return definitions;
    }

    /// <summary>
    /// 一条引用固定的目标 revision；快照中没有这条引用时抛出 DurableExecutionConflict。
    /// The target revision pinned for one reference; throws DurableExecutionConflict when the snapshot has no such reference.
    /// </summary>
    public int GetTargetRevision(Guid parentAgentflowId, int parentRevision, string parentNodeId, string? blockMemberId)
    {
        var matching = Dependencies
            .Where(dependency =>
                dependency.ParentAgentflowId == parentAgentflowId
                && dependency.ParentRevision == parentRevision
                && dependency.ParentNodeId == parentNodeId
                && dependency.BlockMemberId == blockMemberId
            )
            .Select(dependency => dependency.TargetRevision)
            .Distinct()
            .ToArray();
        if (matching.Length != 1)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }

        return matching[0];
    }

    private static AgentflowDependencyRevision[] OrderDependencies(
        IEnumerable<AgentflowDependencyRevision> dependencies
    ) =>
        dependencies
            .OrderBy(item => item.ParentAgentflowId)
            .ThenBy(item => item.ParentRevision)
            .ThenBy(item => item.ParentNodeId, StringComparer.Ordinal)
            .ThenBy(item => item.BlockMemberId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetAgentflowId)
            .ThenBy(item => item.TargetRevision)
            .ToArray();
}
