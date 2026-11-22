using System.Data.Common;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Facades;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Projects.Infrastructure;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Projects.Tests;

public sealed partial class ProjectDeletionCoordinatorTests
{
    private static AgentDeletionCoordinator CreateAgentDeletionCoordinator(AgwDbContext context) =>
        new(
            context,
            InMemoryApplicationLock.Shared,
            new AgentflowRevisionUsageReader(
                context,
                new ConversationTurnStore(context, TimeProvider.System),
                new AgentflowRevisionSnapshotReader(context)
            )
        );

    [Theory]
    [InlineData("workflow")]
    [InlineData("enabled-job-subscription")]
    [InlineData("disabled-job-subscription")]
    public async Task DeleteAgentAsync_DefinitionReference_RejectsWithoutDeletingState(string reference)
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        if (reference == "workflow")
        {
            var flow = new Agentflow
            {
                Id = Guid.NewGuid(),
                CreateBy = "tester",
                Name = "dependent",
            };
            context.Agentflows.Add(flow);
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = flow.Id,
                    NodeId = "agent",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agent.Id,
                }
            );
        }
        else if (reference is "enabled-job-subscription" or "disabled-job-subscription")
        {
            context.EventSubscriptions.Add(
                new EventSubscription
                {
                    Id = Guid.CreateVersion7(),
                    CreateBy = "tester",
                    ProjectId = project.Id,
                    TargetKind = EventSubscriptionTargetKind.Agent,
                    AgentId = agent.Id,
                    ManagedBy = EventManagedBy.Job,
                    ManagedKey = Guid.CreateVersion7().ToString("D"),
                    Enabled = reference == "enabled-job-subscription",
                }
            );
        }
        await context.SaveChangesAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.Equal(ErrorCodes.AgentInUse.Code, error.Code);
        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
        Assert.Equal(1, await context.AgentSessionStates.CountAsync(token));
        Assert.Equal(1, await context.ProjectConversationBindings.CountAsync(token));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeleteAgentAsync_LatestOrPublishedBlockMemberReference_Rejects(int revision)
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, _, _) = await SeedAgentStateAsync(context);
        var now = TimeProvider.System.GetUtcNow();
        var flowId = Guid.CreateVersion7();
        context.Agentflows.Add(
            new Agentflow
            {
                Id = flowId,
                CreateBy = "tester",
                Name = "member reference",
                LatestRevision = 2,
                PublishedRevision = 1,
            }
        );
        context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = flowId,
                Revision = 1,
                CreateBy = "tester",
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = flowId,
                Revision = 2,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        context.AgentflowBlockMembers.Add(
            new AgentflowBlockMember
            {
                AgentflowId = flowId,
                AgentflowRevision = revision,
                NodeId = "block",
                MemberId = "member-1",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agent.Id,
                Name = "Runner",
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        await context.SaveChangesAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.Equal(ErrorCodes.AgentInUse.Code, error.Code);
        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAgentAsync_PinnedHistoricalRevisionReference_Rejects(bool blockMember)
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        var now = TimeProvider.System.GetUtcNow();
        var flowId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        context.Agentflows.Add(
            new Agentflow
            {
                Id = flowId,
                CreateBy = "tester",
                Name = "pinned historical revision",
                LatestRevision = 2,
                PublishedRevision = 2,
            }
        );
        context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = flowId,
                Revision = 1,
                CreateBy = "tester",
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = flowId,
                Revision = 2,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        if (blockMember)
        {
            context.AgentflowBlockMembers.Add(
                new AgentflowBlockMember
                {
                    AgentflowId = flowId,
                    AgentflowRevision = 1,
                    NodeId = "block",
                    MemberId = "member-1",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agent.Id,
                    Name = "Agent",
                }
            );
        }
        else
        {
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = flowId,
                    AgentflowRevision = 1,
                    NodeId = "agent",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agent.Id,
                }
            );
        }
        var manifest = new DurableExecutionManifest
        {
            TurnId = turnId,
            UserId = "tester",
            WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(project.Id, null),
            AgentId = flowId,
            AgentType = AgentRuntimeType.Agentflow,
            AgentflowRevision = 1,
            AgentflowDependencies = [],
            Input = new AgwUserInput { Contents = [] },
            Task = new DurableProjectTaskSnapshot
            {
                TaskId = Guid.CreateVersion7(),
                ProjectConversationId = conversation.Id,
                ProjectId = project.Id,
                ContextId = conversation.ContextId,
            },
            Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
        };
        context.DurableExecutions.Add(
            new DurableExecutionRecord
            {
                Id = turnId,
                UserId = "tester",
                ProjectId = project.Id,
                ProjectConversationId = conversation.Id,
                ScopeBackfilled = true,
                ManifestJson = JsonUtil.Serialize(manifest),
                Status = DurableExecutionStatus.Running,
                StateChangedAt = now,
                StateVersion = Guid.CreateVersion7(),
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        await context.SaveChangesAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.Equal(ErrorCodes.AgentInUse.Code, error.Code);
        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
    }

    [Fact]
    public async Task DeleteAgentAsync_ResumableCheckpointPinsNestedHistoricalRevision_Rejects()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        var now = TimeProvider.System.GetUtcNow();
        var turnId = Guid.CreateVersion7();
        var dependency = SeedNestedHistoricalAgentflow(context, agent.Id);
        context.ProjectConversationTurns.Add(CreateCompletedSourceTurn(turnId, conversation, dependency, now));
        context.DurableExecutions.Add(CreateCompletedExecution(turnId, project, conversation, dependency, now));
        var checkpoint = CreateDurableCheckpoint(turnId, project, conversation, dependency, now);
        context.AgentflowCheckpoints.Add(checkpoint);
        await context.SaveChangesAsync(token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.Equal(ErrorCodes.AgentInUse.Code, exception.Code);
        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));

        context.AgentflowCheckpoints.Remove(checkpoint);
        await context.SaveChangesAsync(token);
        Assert.True(await CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token));
        Assert.False(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
    }

    private static AgentflowDependencyRevision SeedNestedHistoricalAgentflow(AgwDbContext context, Guid agentId)
    {
        var rootId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var dependency = new AgentflowDependencyRevision(rootId, 1, "nested", null, targetId, 1);
        context.Agentflows.AddRange(
            new Agentflow
            {
                Id = rootId,
                CreateBy = "tester",
                Name = "checkpoint root",
                LatestRevision = 2,
                PublishedRevision = 2,
            },
            new Agentflow
            {
                Id = targetId,
                CreateBy = "tester",
                Name = "nested historical target",
                LatestRevision = 2,
                PublishedRevision = 2,
            }
        );
        AddAgentflowRevisionRows(context, rootId, targetId);
        AddNestedReferenceNodes(context, rootId, targetId, agentId);
        return dependency;
    }

    private static void AddAgentflowRevisionRows(AgwDbContext context, Guid rootId, Guid targetId) =>
        context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = rootId,
                Revision = 1,
                CreateBy = "tester",
            },
            new AgentflowRevision
            {
                AgentflowId = rootId,
                Revision = 2,
                CreateBy = "tester",
            },
            new AgentflowRevision
            {
                AgentflowId = targetId,
                Revision = 1,
                CreateBy = "tester",
            },
            new AgentflowRevision
            {
                AgentflowId = targetId,
                Revision = 2,
                CreateBy = "tester",
            }
        );

    private static void AddNestedReferenceNodes(AgwDbContext context, Guid rootId, Guid targetId, Guid agentId) =>
        context.AgentflowNodes.AddRange(
            new AgentflowNode
            {
                AgentflowId = rootId,
                AgentflowRevision = 1,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = targetId,
            },
            new AgentflowNode
            {
                AgentflowId = targetId,
                AgentflowRevision = 1,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            }
        );

    private static ProjectConversationTurn CreateCompletedSourceTurn(
        Guid turnId,
        ProjectConversation conversation,
        AgentflowDependencyRevision dependency,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = turnId,
            ProjectConversationId = conversation.Id,
            TargetId = dependency.ParentAgentflowId,
            RuntimeType = AgentRuntimeType.Agentflow,
            Status = ProjectConversationTurnStatus.Completed,
            InputMessageId = Guid.CreateVersion7(),
            FirstSequence = 0,
            StartedAt = now,
            FinishedAt = now,
            UsageSettlesAt = now,
            Extra = new ProjectConversationTurnExtra
            {
                AgentflowRevision = dependency.ParentRevision,
                AgentflowDependencies =
                [
                    new ProjectConversationTurnAgentflowDependency(
                        dependency.ParentAgentflowId,
                        dependency.ParentRevision,
                        dependency.ParentNodeId,
                        dependency.BlockMemberId,
                        dependency.TargetAgentflowId,
                        dependency.TargetRevision
                    ),
                ],
            },
        };

    private static DurableExecutionRecord CreateCompletedExecution(
        Guid turnId,
        Project project,
        ProjectConversation conversation,
        AgentflowDependencyRevision dependency,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = turnId,
            UserId = "tester",
            ProjectId = project.Id,
            ProjectConversationId = conversation.Id,
            ScopeBackfilled = true,
            ManifestJson = JsonUtil.Serialize(
                new DurableExecutionManifest
                {
                    TurnId = turnId,
                    UserId = "tester",
                    WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(project.Id, null),
                    AgentId = dependency.ParentAgentflowId,
                    AgentType = AgentRuntimeType.Agentflow,
                    AgentflowRevision = dependency.ParentRevision,
                    AgentflowDependencies = [dependency],
                    Input = new AgwUserInput { Contents = [] },
                    Task = new DurableProjectTaskSnapshot
                    {
                        TaskId = Guid.CreateVersion7(),
                        ProjectConversationId = conversation.Id,
                        ProjectId = project.Id,
                        ContextId = conversation.ContextId,
                    },
                    Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
                }
            ),
            Status = DurableExecutionStatus.Completed,
            StateChangedAt = now,
            StateVersion = Guid.CreateVersion7(),
            CreateBy = "tester",
            CreateTime = now,
        };

    private static AgentflowCheckpointRecord CreateDurableCheckpoint(
        Guid turnId,
        Project project,
        ProjectConversation conversation,
        AgentflowDependencyRevision dependency,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            SourceTurnId = turnId,
            ProjectId = project.Id,
            ProjectConversationId = conversation.Id,
            ContextId = conversation.ContextId,
            TaskId = Guid.CreateVersion7(),
            AgentflowId = dependency.ParentAgentflowId,
            AgentflowRevision = dependency.ParentRevision,
            RuntimeVersion = 3,
            UserId = "tester",
            IsDurable = true,
            BoundarySequence = 1,
            MarkersJson = "[]",
            CheckpointJson = "{}",
            CreateBy = "tester",
            CreateTime = now,
        };

    [Fact]
    public async Task DeleteAgentAsync_UserSubscriptionReference_DeletesAgent()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, _) = await SeedAgentStateAsync(context);
        context.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = Guid.CreateVersion7(),
                CreateBy = "tester",
                ProjectId = project.Id,
                TargetKind = EventSubscriptionTargetKind.Agent,
                AgentId = agent.Id,
                Enabled = true,
            }
        );
        await context.SaveChangesAsync(token);

        var deleted = await CreateAgentDeletionCoordinator(context).DeleteAsync(agent.Id, "tester", token);

        Assert.True(deleted);
        Assert.False(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
    }

    [Fact]
    public async Task DeleteAgentAsync_Unreferenced_CleansStateAndRejectsLateCallbacks()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        var deletion = CreateAgentDeletionCoordinator(context);
        Assert.False(await deletion.DeleteAsync(agent.Id, "foreign", token));
        Assert.False(await deletion.DeleteAsync(Guid.NewGuid(), "foreign", token));

        Assert.True(await deletion.DeleteAsync(agent.Id, "tester", token));

        using (UserInfoUtil.PushSystemScope())
        {
            Assert.Empty(await context.AgentSessionStates.ToListAsync(token));
            Assert.Empty(await context.ProjectConversationBindings.ToListAsync(token));
        }
        context.ChangeTracker.Clear();
        Assert.False(
            await new AgentSessionStatePersistence(context).SaveAsync(
                project.Id,
                conversation.Id,
                agent.Id,
                "",
                "late-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token
            )
        );
        await using var providerSessions = TestProjectPersistence.CreateProviderSessionServices(options);
        var bindings = TestProjectPersistence.CreateBindingService(
            context,
            providerSessions,
            new AgentCatalogFacade(context, new TestUserInfoService())
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            bindings.UpsertAsync(project.Id, conversation.ContextId, agent.Id, "codex", "late-binding", "tester", token)
        );
        Assert.Equal(1, await context.ProjectConversations.CountAsync(token));
    }

    [Fact]
    public async Task DeleteAgentAsync_FailureAfterStateCleanup_RollsBackAllDeletes()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, _, _) = await SeedAgentStateAsync(context);
        var failingOptions = new DbContextOptionsBuilder<AgwDbContext>(options)
            .AddInterceptors(new FailAgentDelete())
            .Options;
        await using var failingContext = new AgwDbContext(failingOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateAgentDeletionCoordinator(failingContext).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
        Assert.Equal(1, await context.AgentSessionStates.CountAsync(token));
        Assert.Equal(1, await context.ProjectConversationBindings.CountAsync(token));
    }

    private static async Task<(Agent Agent, Project Project, ProjectConversation Conversation)> SeedAgentStateAsync(
        AgwDbContext context
    )
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "agent",
            CreateBy = "tester",
        };
        var project = new Project { Id = Guid.NewGuid(), CreateBy = "tester" };
        var conversation = new ProjectConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ContextId = "agent-state",
            CreateBy = "tester",
        };
        context.AddRange(agent, project, conversation);
        context.AgentSessionStates.Add(
            new AgentSessionStateEntry
            {
                AgentId = agent.Id,
                ProjectConversationId = conversation.Id,
                SerializedSession = "{}",
            }
        );
        context.ProjectConversationBindings.Add(
            new ProjectConversationBinding
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                ProjectConversationId = conversation.Id,
                ExternalAgentName = "codex",
                ProviderSessionId = "session",
                CreateBy = "tester",
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (agent, project, conversation);
    }

    private sealed class FailAgentDelete : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.StartsWith("DELETE FROM \"agent\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected deletion failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
