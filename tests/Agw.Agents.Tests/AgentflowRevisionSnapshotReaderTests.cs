using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Persistence;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class AgentflowRevisionSnapshotReaderTests
{
    private const string Owner = "tester";

    [Fact]
    public async Task CreateRevisionSnapshotAsync_RepeatedReferencesToSameTarget_UsesOneTargetRevisionAndRecordsEachReference()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        AddFlow(database, targetId, "target", 2, 1, 2, now);
        database.Context.AgentflowNodes.AddRange(
            CreateWorkflowNode(rootId, 1, "workflow-a", targetId, now),
            CreateWorkflowNode(rootId, 1, "workflow-b", targetId, now)
        );
        database.Context.AgentflowBlockMembers.Add(
            new AgentflowBlockMember
            {
                AgentflowId = rootId,
                AgentflowRevision = 1,
                NodeId = "block",
                MemberId = "member-1",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = targetId,
                Name = "Nested workflow",
                CreateBy = Owner,
                CreateTime = now,
            }
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var snapshot = await CreateReader(database)
            .CreateRevisionSnapshotAsync(rootId, 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.RootRevision);
        Assert.Equal(3, snapshot.Dependencies.Count);
        Assert.All(snapshot.Dependencies, dependency => Assert.Equal(2, dependency.TargetRevision));
        Assert.Equal(
            ["block", "workflow-a", "workflow-b"],
            snapshot.Dependencies.Select(dependency => dependency.ParentNodeId).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            "member-1",
            Assert.Single(snapshot.Dependencies, dependency => dependency.BlockMemberId is not null).BlockMemberId
        );
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_TransitiveWorkflowReferences_RecordsFullClosure()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var nestedId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        AddFlow(database, nestedId, "nested", 2, 1, 2, now);
        AddFlow(database, targetId, "target", 4, now);
        database.Context.AgentflowNodes.AddRange(
            CreateWorkflowNode(rootId, 1, "nested-node", nestedId, now),
            CreateWorkflowNode(nestedId, 2, "target-node", targetId, now)
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var snapshot = await CreateReader(database)
            .CreateRevisionSnapshotAsync(rootId, 0, TestContext.Current.CancellationToken);

        Assert.Contains(
            snapshot.Dependencies,
            dependency =>
                dependency.ParentAgentflowId == rootId
                && dependency.ParentRevision == 1
                && dependency.TargetAgentflowId == nestedId
                && dependency.TargetRevision == 2
        );
        Assert.Contains(
            snapshot.Dependencies,
            dependency =>
                dependency.ParentAgentflowId == nestedId
                && dependency.ParentRevision == 2
                && dependency.TargetAgentflowId == targetId
                && dependency.TargetRevision == 4
        );
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_ExplicitRevision_UsesItForRoot()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, 1, 2, now);
        AddFlow(database, targetId, "target", 1, now);
        database.Context.AgentflowNodes.AddRange(
            CreateWorkflowNode(rootId, 1, "old-target", targetId, now),
            CreateWorkflowNode(rootId, 2, "new-target", targetId, now)
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var snapshot = await CreateReader(database)
            .CreateRevisionSnapshotAsync(rootId, 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.RootRevision);
        Assert.Equal("new-target", Assert.Single(snapshot.Dependencies).ParentNodeId);
    }

    [Theory]
    [InlineData(-1, 4000001)]
    [InlineData(0, 4090039)]
    [InlineData(7, 4040018)]
    public async Task CreateRevisionSnapshotAsync_RootRevisionRules_ThrowExpectedErrors(
        int requestedRevision,
        int expectedCode
    )
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 0, 1, 2, now);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateReader(database)
                .CreateRevisionSnapshotAsync(rootId, requestedRevision, TestContext.Current.CancellationToken)
        );

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_WithoutCurrentOwner_ThrowsAuthenticationRequired()
    {
        using var database = new TestAgentDatabase();
        using var userScope = UserInfoUtil.Push(null);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateReader(database)
                .CreateRevisionSnapshotAsync(Guid.CreateVersion7(), 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, exception.Code);
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_ForeignAgentflow_ReturnsNotFound()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        AddFlow(database, rootId, "other-user", 1, DateTimeOffset.UtcNow, "other-user");
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateReader(database).CreateRevisionSnapshotAsync(rootId, 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_NestedTargetNotPublished_ThrowsNotPublished()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        AddFlow(database, targetId, "target", 0, 1, 2, now);
        database.Context.AgentflowNodes.Add(CreateWorkflowNode(rootId, 1, "nested", targetId, now));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateReader(database).CreateRevisionSnapshotAsync(rootId, 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.AgentflowNotPublished.Code, exception.Code);
    }

    [Fact]
    public async Task CreateRevisionSnapshotAsync_NestedReferenceBackToRoot_ThrowsInvalidParam()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var nestedId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, 1, 2, now);
        AddFlow(database, nestedId, "nested", 1, now);
        database.Context.AgentflowNodes.AddRange(
            CreateWorkflowNode(rootId, 2, "nested", nestedId, now),
            CreateWorkflowNode(nestedId, 1, "root", rootId, now)
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateReader(database).CreateRevisionSnapshotAsync(rootId, 2, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task DefinitionsRemainAvailableAsync_CompleteSnapshotAndReferences_ReturnsTrue()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var nestedId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        AddFlow(database, nestedId, "nested", 1, now);
        database.Context.Agents.Add(
            new Agent
            {
                Id = agentId,
                Name = "runner",
                DisplayName = "Runner",
                Type = AgentType.System,
                CreateBy = Owner,
                CreateTime = now,
            }
        );
        database.Context.AgentflowNodes.AddRange(
            new AgentflowNode
            {
                AgentflowId = rootId,
                AgentflowRevision = 1,
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
                CreateBy = Owner,
                CreateTime = now,
            },
            CreateWorkflowNode(rootId, 1, "nested", nestedId, now)
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var snapshot = new AgentflowRevisionSnapshot(
            1,
            [new AgentflowDependencyRevision(rootId, 1, "nested", null, nestedId, 1)]
        );

        var result = await CreateReader(database)
            .DefinitionsRemainAvailableAsync(rootId, snapshot, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task DefinitionsRemainAvailableAsync_AgentReferenceMissing_ReturnsFalse()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        database.Context.AgentflowNodes.Add(
            new AgentflowNode
            {
                AgentflowId = rootId,
                AgentflowRevision = 1,
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
                CreateBy = Owner,
                CreateTime = now,
            }
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateReader(database)
            .DefinitionsRemainAvailableAsync(
                rootId,
                new AgentflowRevisionSnapshot(1, []),
                TestContext.Current.CancellationToken
            );

        Assert.False(result);
    }

    [Fact]
    public async Task DefinitionsRemainAvailableAsync_NestedReferenceMissingFromSnapshot_ReturnsFalse()
    {
        using var database = new TestAgentDatabase();
        using var userScope = PushOwner();
        var rootId = Guid.CreateVersion7();
        var nestedId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        AddFlow(database, rootId, "root", 1, now);
        AddFlow(database, nestedId, "nested", 1, now);
        database.Context.AgentflowNodes.Add(CreateWorkflowNode(rootId, 1, "nested", nestedId, now));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateReader(database)
            .DefinitionsRemainAvailableAsync(
                rootId,
                new AgentflowRevisionSnapshot(1, []),
                TestContext.Current.CancellationToken
            );

        Assert.False(result);
    }

    private static AgentflowRevisionSnapshotReader CreateReader(TestAgentDatabase database) => new(database.Context);

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "test"))
        );

    private static AgentflowNode CreateWorkflowNode(
        Guid agentflowId,
        int revision,
        string nodeId,
        Guid targetId,
        DateTimeOffset now
    ) =>
        new()
        {
            AgentflowId = agentflowId,
            AgentflowRevision = revision,
            NodeId = nodeId,
            Kind = AgentflowNodeKind.WorkflowAsAgent,
            RelateId = targetId,
            CreateBy = Owner,
            CreateTime = now,
        };

    private static void AddFlow(
        TestAgentDatabase database,
        Guid id,
        string name,
        int publishedRevision,
        DateTimeOffset now,
        string owner = Owner
    )
    {
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = id,
                Name = name,
                LatestRevision = publishedRevision,
                PublishedRevision = publishedRevision,
                CreateBy = owner,
                CreateTime = now,
            }
        );
        if (publishedRevision > 0)
        {
            database.Context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = id,
                    Revision = publishedRevision,
                    CreateBy = owner,
                    CreateTime = now,
                }
            );
        }
    }

    private static void AddFlow(
        TestAgentDatabase database,
        Guid id,
        string name,
        int publishedRevision,
        int firstRevision,
        int secondRevision,
        DateTimeOffset now,
        string owner = Owner
    )
    {
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = id,
                Name = name,
                LatestRevision = secondRevision,
                PublishedRevision = publishedRevision,
                CreateBy = owner,
                CreateTime = now,
            }
        );
        database.Context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = id,
                Revision = firstRevision,
                CreateBy = owner,
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = id,
                Revision = secondRevision,
                CreateBy = owner,
                CreateTime = now,
            }
        );
    }
}
