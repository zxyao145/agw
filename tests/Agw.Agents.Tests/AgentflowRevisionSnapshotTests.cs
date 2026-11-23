using Agw.Agents.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class AgentflowRevisionSnapshotTests
{
    [Fact]
    public void FromTurn_CompleteDependencySnapshot_PreservesRootAndDependencies()
    {
        var rootAgentflowId = Guid.CreateVersion7();
        var nestedAgentflowId = Guid.CreateVersion7();
        var dependency = new AgentflowDependencyRevisionDto(
            rootAgentflowId,
            4,
            "nested-node",
            "member-a",
            nestedAgentflowId,
            2
        );
        var turn = CreateTurn(rootAgentflowId, [dependency]);

        var snapshot = AgentflowRevisionSnapshot.FromTurn(turn);

        Assert.Equal(4, snapshot.RootRevision);
        Assert.Single(snapshot.Dependencies);
        Assert.True(snapshot.PinsDefinition(rootAgentflowId, rootAgentflowId, 4));
        Assert.True(snapshot.PinsDefinition(rootAgentflowId, nestedAgentflowId, 2));
        Assert.False(snapshot.PinsDefinition(rootAgentflowId, nestedAgentflowId, 3));
        Assert.Equal(2, snapshot.GetDefinitions(rootAgentflowId).Count);
    }

    [Fact]
    public void FromTurn_MissingDependencySnapshot_ThrowsDurableExecutionConflict()
    {
        var exception = Assert.Throws<AgwException>(() =>
            AgentflowRevisionSnapshot.FromTurn(CreateTurn(Guid.CreateVersion7(), dependencies: null))
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public void MatchesDependencies_IgnoresStorageOrdering()
    {
        var parent = Guid.CreateVersion7();
        var firstTarget = Guid.CreateVersion7();
        var secondTarget = Guid.CreateVersion7();
        var snapshot = new AgentflowRevisionSnapshot(
            3,
            [
                new AgentflowDependencyRevision(parent, 3, "node-a", null, firstTarget, 2),
                new AgentflowDependencyRevision(parent, 3, "node-b", "member-b", secondTarget, 5),
            ]
        );

        Assert.True(
            snapshot.MatchesDependencies([
                new AgentflowDependencyRevision(parent, 3, "node-b", "member-b", secondTarget, 5),
                new AgentflowDependencyRevision(parent, 3, "node-a", null, firstTarget, 2),
            ])
        );
        Assert.False(snapshot.MatchesDependencies(null));
    }

    private static ConversationTurnSnapshot CreateTurn(
        Guid targetId,
        IReadOnlyList<AgentflowDependencyRevisionDto>? dependencies
    ) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            targetId,
            ConversationTurnTargetType.Agentflow,
            ConversationTurnStatus.Accepted,
            Guid.CreateVersion7(),
            1,
            null,
            0,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            4,
            dependencies
        );
}
