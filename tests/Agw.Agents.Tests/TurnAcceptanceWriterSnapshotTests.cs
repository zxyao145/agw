using System.Data;
using System.Data.Common;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agw.Agents.Tests;

public sealed class TurnAcceptanceWriterSnapshotTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(DurableTestConnections.PostgresConnectionString);

    [Fact]
    public async Task TurnAcceptanceWriter_AcceptAsync_UsesOneSnapshotForRootAndDependencies()
    {
        using var userScope = TurnPersistenceTestKit.EnterUser();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var task = await kit.SeedConversationAsync();
        var flows = await SeedNestedFlowsAsync(kit);
        var turnId = Guid.CreateVersion7();
        var scopeFactory = new CapturingServiceScopeFactory(kit.Services.GetRequiredService<IServiceScopeFactory>());
        var writer = new TurnAcceptanceWriter(scopeFactory, kit.Locks, kit.Clock);
        var expectedDependency = new AgentflowDependencyRevision(flows.RootId, 1, "nested", null, flows.TargetId, 1);

        var result = await writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                (snapshot, _) =>
                {
                    Assert.NotNull(snapshot);
                    Assert.Equal(1, snapshot.RootRevision);
                    Assert.Equal(expectedDependency, Assert.Single(snapshot.Dependencies));
                    var dbContext = Assert.IsType<AgwDbContext>(
                        scopeFactory.LastScope!.ServiceProvider.GetRequiredService<AgwDbContext>()
                    );
                    var transaction = Assert.IsAssignableFrom<IDbContextTransaction>(
                        dbContext.Database.CurrentTransaction
                    );
                    Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
                    return Task.FromResult(
                        new TurnAcceptanceRegistration(
                            [],
                            CreateDurableRegistration(kit, task, flows.RootId, turnId, snapshot)
                        )
                    );
                }
            ),
            cancellationToken
        );

        Assert.True(result.Created);
        await AssertPersistedSnapshotAsync(kit, turnId, 1, expectedDependency);
    }

    [Fact]
    public async Task TurnAcceptanceWriter_ExistingTurnId_ReusesStoredRevisionSnapshot()
    {
        using var userScope = TurnPersistenceTestKit.EnterUser();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var task = await kit.SeedConversationAsync();
        var flows = await SeedNestedFlowsAsync(kit);
        var turnId = Guid.CreateVersion7();
        var writer = new TurnAcceptanceWriter(
            kit.Services.GetRequiredService<IServiceScopeFactory>(),
            kit.Locks,
            kit.Clock
        );
        var expectedDependency = new AgentflowDependencyRevision(flows.RootId, 1, "nested", null, flows.TargetId, 1);
        var first = await writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                (snapshot, _) =>
                    Task.FromResult(
                        new TurnAcceptanceRegistration(
                            [],
                            CreateDurableRegistration(kit, task, flows.RootId, turnId, snapshot!)
                        )
                    )
            ),
            cancellationToken
        );
        Assert.True(first.Created);
        await UpdatePublishedRevisionAsync(kit, flows.TargetId, 2, cancellationToken);
        var registerCalled = false;

        var replay = await writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                (_, _) =>
                {
                    registerCalled = true;
                    return Task.FromResult(new TurnAcceptanceRegistration([], null));
                }
            ),
            cancellationToken
        );

        Assert.False(replay.Created);
        Assert.False(registerCalled);
        Assert.Equal(1, replay.Turn.AgentflowRevision);
        Assert.Equal(
            [expectedDependency],
            replay.Turn.AgentflowDependencies!.Select(AgentflowDependencyRevisionMapping.FromDto)
        );
        await AssertPersistedSnapshotAsync(kit, turnId, 1, expectedDependency);
    }

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires an isolated PostgreSQL test server.")]
    public async Task Postgres_CreateRevisionSnapshotAsync_PublishDuringSnapshot_ProducesConsistentDependencyClosure()
    {
        using var userScope = TurnPersistenceTestKit.EnterUser();
        var cancellationToken = TestContext.Current.CancellationToken;
        var rootReadGate = new AgentflowRootReadGate();
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            DurableTestConnections.PostgresConnectionString!,
            interceptors: [rootReadGate]
        );
        var task = await kit.SeedConversationAsync();
        var flows = await SeedNestedFlowsAsync(kit);
        rootReadGate.Arm(flows.RootId);
        var turnId = Guid.CreateVersion7();
        var scopeFactory = new CapturingServiceScopeFactory(kit.Services.GetRequiredService<IServiceScopeFactory>());
        var writer = new TurnAcceptanceWriter(scopeFactory, kit.Locks, kit.Clock);
        var writeTask = writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                (snapshot, _) =>
                {
                    Assert.NotNull(snapshot);
                    Assert.Equal(
                        IsolationLevel.Serializable,
                        scopeFactory
                            .LastScope!.ServiceProvider.GetRequiredService<AgwDbContext>()
                            .Database.CurrentTransaction!.GetDbTransaction()
                            .IsolationLevel
                    );
                    return Task.FromResult(
                        new TurnAcceptanceRegistration(
                            [],
                            CreateDurableRegistration(kit, task, flows.RootId, turnId, snapshot)
                        )
                    );
                }
            ),
            cancellationToken
        );

        try
        {
            await rootReadGate.RootReadCompleted.Task.WaitAsync(WaitTimeout, cancellationToken);
            await UpdatePublishedRevisionAsync(kit, flows.TargetId, 2, cancellationToken);
        }
        finally
        {
            rootReadGate.Continue.TrySetResult();
        }

        var exception = await Record.ExceptionAsync(() => writeTask);
        if (FindSerializationFailure(exception) is not null)
        {
            Assert.Equal(0, await kit.CountTurnsAsync(task.ProjectConversationId));
            return;
        }

        Assert.Null(exception);
        var expectedDependency = new AgentflowDependencyRevision(flows.RootId, 1, "nested", null, flows.TargetId, 1);
        await AssertPersistedSnapshotAsync(kit, turnId, 1, expectedDependency);
    }

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires an isolated PostgreSQL test server.")]
    public async Task Postgres_TurnAcceptanceWriter_SerializationFailure_PropagatesAndWritesNoTurn()
    {
        using var userScope = TurnPersistenceTestKit.EnterUser();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            DurableTestConnections.PostgresConnectionString!
        );
        var task = await kit.SeedConversationAsync();
        var flows = await SeedNestedFlowsAsync(kit);
        var turnId = Guid.CreateVersion7();
        var writer = new TurnAcceptanceWriter(
            kit.Services.GetRequiredService<IServiceScopeFactory>(),
            kit.Locks,
            kit.Clock
        );
        var registerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeTask = writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                async (snapshot, token) =>
                {
                    Assert.NotNull(snapshot);
                    registerEntered.TrySetResult();
                    await continueRegistration.Task.WaitAsync(WaitTimeout, token);
                    return new TurnAcceptanceRegistration(
                        [],
                        CreateDurableRegistration(kit, task, flows.RootId, turnId, snapshot)
                    );
                }
            ),
            cancellationToken
        );

        try
        {
            await registerEntered.Task.WaitAsync(WaitTimeout, cancellationToken);
            await using var concurrentContext = kit.CreateContext();
            await using var concurrentTransaction = await concurrentContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken
            );
            Assert.Equal(
                0,
                await concurrentContext
                    .ProjectConversationTurns.Where(turn => turn.ProjectConversationId == task.ProjectConversationId)
                    .CountAsync(cancellationToken)
            );
            var root = await concurrentContext.Agentflows.SingleAsync(
                flow => flow.Id == flows.RootId,
                cancellationToken
            );
            root.PublishedRevision = 2;
            await concurrentContext.SaveChangesAsync(cancellationToken);
            await concurrentTransaction.CommitAsync(cancellationToken);
        }
        finally
        {
            continueRegistration.TrySetResult();
        }

        var exception = await Record.ExceptionAsync(() => writeTask);
        Assert.NotNull(exception);
        var serializationFailure = FindSerializationFailure(exception);
        Assert.NotNull(serializationFailure);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, serializationFailure.SqlState);
        Assert.Equal(0, await kit.CountTurnsAsync(task.ProjectConversationId));
        await using (var context = kit.CreateContext())
        {
            Assert.False(
                await context
                    .DurableExecutions.IgnoreQueryFilters()
                    .AnyAsync(execution => execution.Id == turnId, cancellationToken)
            );
        }

        var retry = await writer.AcceptAsync(
            CreateWrite(
                task,
                flows.RootId,
                turnId,
                (snapshot, _) =>
                    Task.FromResult(
                        new TurnAcceptanceRegistration(
                            [],
                            CreateDurableRegistration(kit, task, flows.RootId, turnId, snapshot!)
                        )
                    )
            ),
            cancellationToken
        );

        Assert.True(retry.Created);
        Assert.Equal(2, retry.Turn.AgentflowRevision);
        var expectedDependency = new AgentflowDependencyRevision(flows.RootId, 2, "nested", null, flows.TargetId, 1);
        Assert.Equal(
            [expectedDependency],
            retry.Turn.AgentflowDependencies!.Select(AgentflowDependencyRevisionMapping.FromDto)
        );
        await AssertPersistedSnapshotAsync(kit, turnId, 2, expectedDependency);
    }

    private static TurnAcceptanceWrite CreateWrite(
        AgentExecutionTask task,
        Guid rootAgentflowId,
        Guid turnId,
        Func<AgentflowRevisionSnapshot?, CancellationToken, Task<TurnAcceptanceRegistration>> register
    ) =>
        new()
        {
            RequestedAgentflowRevision = 0,
            Turn = new AcceptConversationTurnRequest
            {
                TurnId = turnId,
                ProjectId = task.ProjectId,
                ContextId = task.ContextId,
                ConversationId = task.ProjectConversationId,
                Generation = task.Generation,
                TaskId = task.TaskId,
                TargetId = rootAgentflowId,
                TargetType = ConversationTurnTargetType.Agentflow,
            },
            Register = register,
        };

    private static DurableTurnRegistration CreateDurableRegistration(
        TurnPersistenceTestKit kit,
        AgentExecutionTask task,
        Guid rootAgentflowId,
        Guid turnId,
        AgentflowRevisionSnapshot snapshot
    )
    {
        var target = new ExecutionTarget(rootAgentflowId, AgentRuntimeType.Agentflow, snapshot.RootRevision);
        var envelope = TurnPersistenceTestKit.CreateEnvelope(turnId, task, target);
        var request = new ExecutionRequest(
            turnId,
            TurnPersistenceTestKit.UserId,
            target,
            task,
            TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
            new AgwUserInput { Author = "user", Contents = [] },
            true,
            new ProjectWorkspaceSnapshot(AppContext.BaseDirectory, [], "snapshot-test")
        )
        {
            AgentflowSnapshot = snapshot,
            Envelope = envelope,
        };
        return kit.Coordinator.CreateRegistration(request, TurnMessageFactory.CreateStarted(envelope));
    }

    private static async Task<FlowPair> SeedNestedFlowsAsync(TurnPersistenceTestKit kit)
    {
        var rootId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var now = kit.Clock.GetUtcNow();
        await kit.SeedAgentflowAsync(rootId);
        await kit.SeedAgentflowAsync(targetId);
        await using var context = kit.CreateContext();
        var root = await context.Agentflows.SingleAsync(
            flow => flow.Id == rootId,
            TestContext.Current.CancellationToken
        );
        var target = await context.Agentflows.SingleAsync(
            flow => flow.Id == targetId,
            TestContext.Current.CancellationToken
        );
        root.LatestRevision = 2;
        target.LatestRevision = 2;
        context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = rootId,
                Revision = 2,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = targetId,
                Revision = 2,
                CreateBy = TurnPersistenceTestKit.UserId,
                CreateTime = now,
            }
        );
        context.AgentflowNodes.AddRange(
            CreateNestedNode(rootId, 1, targetId, now),
            CreateNestedNode(rootId, 2, targetId, now)
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new FlowPair(rootId, targetId);
    }

    private static AgentflowNode CreateNestedNode(Guid rootId, int revision, Guid targetId, DateTimeOffset now) =>
        new()
        {
            AgentflowId = rootId,
            AgentflowRevision = revision,
            NodeId = "nested",
            Kind = AgentflowNodeKind.WorkflowAsAgent,
            Name = "Nested",
            RelateId = targetId,
            RoutingStrategy = AgentflowRoutingStrategy.Direct,
            ConfigJson = "{}",
            CreateBy = TurnPersistenceTestKit.UserId,
            CreateTime = now,
        };

    private static async Task UpdatePublishedRevisionAsync(
        TurnPersistenceTestKit kit,
        Guid agentflowId,
        int revision,
        CancellationToken cancellationToken
    )
    {
        await using var context = kit.CreateContext();
        await context
            .Agentflows.Where(flow => flow.Id == agentflowId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(flow => flow.PublishedRevision, revision),
                cancellationToken
            );
    }

    private static async Task AssertPersistedSnapshotAsync(
        TurnPersistenceTestKit kit,
        Guid turnId,
        int rootRevision,
        AgentflowDependencyRevision expectedDependency
    )
    {
        var turn = await kit.ReadTurnAsync(turnId);
        Assert.Equal(rootRevision, turn.Extra.AgentflowRevision);
        var turnDependencies = turn
            .Extra.AgentflowDependencies!.Select(dependency => new AgentflowDependencyRevision(
                dependency.ParentAgentflowId,
                dependency.ParentRevision,
                dependency.ParentNodeId,
                dependency.BlockMemberId,
                dependency.TargetAgentflowId,
                dependency.TargetRevision
            ))
            .ToArray();
        Assert.Equal([expectedDependency], turnDependencies);
        var execution = await kit.ReadExecutionAsync(turnId);
        var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            execution.ManifestJson,
            "durable execution manifest"
        );
        Assert.Equal(turn.Extra.AgentflowRevision, manifest.AgentflowRevision);
        Assert.Equal(turnDependencies, manifest.AgentflowDependencies);
    }

    private static PostgresException? FindSerializationFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                return postgres;
            }
        }

        return null;
    }

    private sealed record FlowPair(Guid RootId, Guid TargetId);

    private sealed class CapturingServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;

        public CapturingServiceScopeFactory(IServiceScopeFactory inner)
        {
            _inner = inner;
        }

        public IServiceScope? LastScope { get; private set; }

        public IServiceScope CreateScope()
        {
            LastScope = _inner.CreateScope();
            return LastScope;
        }
    }

    private sealed class AgentflowRootReadGate : DbCommandInterceptor
    {
        private Guid _rootAgentflowId;
        private int _armed;

        public TaskCompletionSource RootReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Guid rootAgentflowId)
        {
            _rootAgentflowId = rootAgentflowId;
            Volatile.Write(ref _armed, 1);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                Volatile.Read(ref _armed) == 1
                && IsAgentflowEntityQuery(command.CommandText)
                && HasParameter(command, _rootAgentflowId)
                && Interlocked.Exchange(ref _armed, 0) == 1
            )
            {
                RootReadCompleted.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        private static bool IsAgentflowEntityQuery(string commandText) =>
            commandText.Contains("FROM \"agentflow\"", StringComparison.OrdinalIgnoreCase)
            || commandText.Contains("FROM agentflow ", StringComparison.OrdinalIgnoreCase);

        private static bool HasParameter(DbCommand command, Guid value) =>
            command
                .Parameters.Cast<DbParameter>()
                .Any(parameter => Guid.TryParse(parameter.Value?.ToString(), out var candidate) && candidate == value);
    }
}
