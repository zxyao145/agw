using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionPermissionTests
{
    public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(DurableTestConnections.PostgresConnectionString);

    [Fact]
    public async Task SetPermissionModeAsync_DurableReconnect_UsesAcceptedUnpublishedRevisionSnapshot()
    {
        using var owner = TurnPersistenceTestKit.EnterUser();
        var clock = new ManualTimeProvider();
        await using var persistence = await TurnPersistenceTestKit.CreateAsync(
            clock: clock,
            configure: services => services.AddScoped<ExecutionPermissionService>()
        );
        var token = TestContext.Current.CancellationToken;
        var task = await persistence.SeedConversationAsync();
        var rootId = Guid.CreateVersion7();
        var dependencyId = Guid.CreateVersion7();
        var codexId = Guid.CreateVersion7();
        var systemId = Guid.CreateVersion7();
        await SeedAgentflowsAsync(persistence, rootId, dependencyId, codexId, systemId);

        var permissions = persistence.ResolveScoped<ExecutionPermissionService>();
        var projectTasks = persistence.CreateProjectTasks();
        var acceptance = persistence.CreateAcceptance(
            projectTasks,
            persistence.CreateProjectRuntime(),
            persistence.Coordinator,
            permissions
        );
        var settings = TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId);
        var accepted = await acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                TurnPersistenceTestKit.UserId,
                null,
                new ExecutionTarget(rootId, AgentRuntimeType.Agentflow),
                task.ProjectConversationId,
                TurnPersistenceTestKit.CreateInput("input"),
                settings,
                Stream: true
            )
            {
                Task = task,
                RequestedAgentflowRevision = 2,
            },
            token
        );

        Assert.Equal(2, accepted.Request.AgentflowSnapshot!.RootRevision);
        Assert.Equal(1, Assert.Single(accepted.Request.AgentflowSnapshot.Dependencies).TargetRevision);

        await PublishNewerRevisionsAsync(persistence, rootId, dependencyId, token);
        Assert.Contains(
            AgwPermissionMode.AlwaysAsk,
            (await permissions.GetAsync(AgentRuntimeType.Agentflow, rootId, token)).SupportedPermissionModes
        );

        await using var connection = new ExecutionConnectionContext(
            TurnPersistenceTestKit.UserId,
            persistence.Broadcasts.GetOrCreate(accepted.Request.TurnId, TurnPersistenceTestKit.UserId),
            CancellationToken.None,
            acceptance,
            projectTasks,
            persistence.CreateConversationAccess(),
            inProcessCoordinators: null,
            durableCoordinator: persistence.Coordinator,
            permissions: permissions
        );
        await connection.ApplySettingsAsync(settings, token);
        await connection.SubscribeTurnAsync(accepted.Request.TurnId, cursor: null, token);
        var originalSettings = connection.Settings;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            connection.SetPermissionModeAsync(AgwPermissionMode.AlwaysAsk, token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Same(originalSettings, connection.Settings);

        var leaseDuration = TimeSpan.FromSeconds(30);
        var firstLease = Assert.IsType<DurableLease>(
            await persistence.Leases.TryClaimAsync(accepted.Request.TurnId, "worker-a", leaseDuration, token)
        );
        var store = persistence.ResolveScoped<DurableExecutionStore>();
        var firstClaim = Assert.IsType<DurableExecutionSnapshot>(
            await store.LoadClaimedAsync(accepted.Request.TurnId, token)
        );

        clock.Advance(leaseDuration + TimeSpan.FromSeconds(1));

        var reclaimedLease = Assert.IsType<DurableLease>(
            await persistence.Leases.TryClaimAsync(accepted.Request.TurnId, "worker-b", leaseDuration, token)
        );
        var reclaimed = Assert.IsType<DurableExecutionSnapshot>(
            await store.LoadClaimedAsync(accepted.Request.TurnId, token)
        );

        Assert.Equal(firstLease.Epoch + 1, reclaimedLease.Epoch);
        Assert.Equal(firstClaim.Manifest.AgentflowRevision, reclaimed.Manifest.AgentflowRevision);
        Assert.Equal(firstClaim.Manifest.AgentflowDependencies, reclaimed.Manifest.AgentflowDependencies);
        Assert.Equal(2, reclaimed.Manifest.AgentflowRevision);
        Assert.Equal(1, Assert.Single(reclaimed.Manifest.AgentflowDependencies!).TargetRevision);

        await using var definitionContext = persistence.CreateContext();
        var dependency = Assert.Single(reclaimed.Manifest.AgentflowDependencies!);
        Assert.Equal(dependencyId, dependency.TargetAgentflowId);
        var pinnedNodes = await new AgentflowDefinitionReader(definitionContext).ListNodesAsync(
            dependency.TargetAgentflowId,
            dependency.TargetRevision,
            token
        );
        var pinnedNode = Assert.Single(pinnedNodes);
        Assert.Equal("codex-agent", pinnedNode.NodeId);
        Assert.Equal(codexId, pinnedNode.RelateId);
    }

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "A PostgreSQL test connection is required.")]
    public async Task Postgres_AcceptedSnapshot_ResolvesNestedDefinitionAtPinnedRevision()
    {
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var persistence = await TurnPersistenceTestKit.CreatePostgresAsync(
            DurableTestConnections.PostgresConnectionString!
        );
        var token = TestContext.Current.CancellationToken;
        var task = await persistence.SeedConversationAsync();
        var rootId = Guid.CreateVersion7();
        var dependencyId = Guid.CreateVersion7();
        var codexId = Guid.CreateVersion7();
        var systemId = Guid.CreateVersion7();
        await SeedAgentflowsAsync(persistence, rootId, dependencyId, codexId, systemId);
        var acceptance = persistence.CreateAcceptance(
            persistence.CreateProjectTasks(),
            persistence.CreateProjectRuntime(),
            persistence.Coordinator
        );
        var accepted = await acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                TurnPersistenceTestKit.UserId,
                null,
                new ExecutionTarget(rootId, AgentRuntimeType.Agentflow),
                task.ProjectConversationId,
                TurnPersistenceTestKit.CreateInput("input"),
                TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                Stream: true
            )
            {
                Task = task,
                RequestedAgentflowRevision = 2,
            },
            token
        );

        await PublishNewerRevisionsAsync(persistence, rootId, dependencyId, token);

        var manifest = (
            await persistence.ResolveScoped<DurableExecutionStore>().GetAsync(accepted.Request.TurnId, token)
        ).Manifest;
        Assert.Equal(2, manifest.AgentflowRevision);
        var dependency = Assert.Single(manifest.AgentflowDependencies!);
        Assert.Equal(1, dependency.TargetRevision);

        await using var definitionContext = persistence.CreateContext();
        var nodes = await new AgentflowDefinitionReader(definitionContext).ListNodesAsync(
            dependency.TargetAgentflowId,
            dependency.TargetRevision,
            token
        );
        var pinnedNode = Assert.Single(nodes);
        Assert.Equal("codex-agent", pinnedNode.NodeId);
        Assert.Equal(codexId, pinnedNode.RelateId);
    }

    private static async Task SeedAgentflowsAsync(
        TurnPersistenceTestKit persistence,
        Guid rootId,
        Guid dependencyId,
        Guid codexId,
        Guid systemId
    )
    {
        await persistence.SeedAgentflowAsync(rootId);
        await persistence.SeedAgentflowAsync(dependencyId);
        var now = persistence.Clock.GetUtcNow();
        await using var db = persistence.CreateContext();
        var root = await db.Agentflows.IgnoreQueryFilters().SingleAsync(item => item.Id == rootId);
        var dependency = await db.Agentflows.IgnoreQueryFilters().SingleAsync(item => item.Id == dependencyId);
        root.LatestRevision = 3;
        dependency.LatestRevision = 2;
        db.Agents.AddRange(
            new Agent
            {
                Id = codexId,
                Name = "codex",
                Type = AgentType.External,
                ExternalAgentKind = EngineKind.Codex,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            },
            new Agent
            {
                Id = systemId,
                Name = "system",
                Type = AgentType.System,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            }
        );
        db.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = rootId,
                Revision = 2,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = rootId,
                Revision = 3,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = dependencyId,
                Revision = 2,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            }
        );
        db.AgentflowNodes.AddRange(
            CreateNode(rootId, 2, "editor-nested", AgentflowNodeKind.WorkflowAsAgent, dependencyId, now),
            CreateNode(rootId, 3, "published-nested", AgentflowNodeKind.WorkflowAsAgent, dependencyId, now),
            CreateNode(dependencyId, 1, "codex-agent", AgentflowNodeKind.Agent, codexId, now),
            CreateNode(dependencyId, 2, "system-agent", AgentflowNodeKind.Agent, systemId, now)
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task PublishNewerRevisionsAsync(
        TurnPersistenceTestKit persistence,
        Guid rootId,
        Guid dependencyId,
        CancellationToken cancellationToken
    )
    {
        await using var db = persistence.CreateContext();
        var root = await db.Agentflows.IgnoreQueryFilters().SingleAsync(item => item.Id == rootId, cancellationToken);
        var dependency = await db
            .Agentflows.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == dependencyId, cancellationToken);
        root.PublishedRevision = 3;
        dependency.PublishedRevision = 2;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static AgentflowNode CreateNode(
        Guid agentflowId,
        int revision,
        string nodeId,
        AgentflowNodeKind kind,
        Guid relateId,
        DateTimeOffset now
    ) =>
        new()
        {
            AgentflowId = agentflowId,
            AgentflowRevision = revision,
            NodeId = nodeId,
            Kind = kind,
            RelateId = relateId,
            Name = nodeId,
            CreateBy = TurnPersistenceTestKit.UserId,
            CreateTime = now,
        };
}
