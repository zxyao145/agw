using System.Globalization;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.History;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Fact]
    public async Task RunAsync_RealCheckpoint_ResumesAndPersistsNodeSessions()
    {
        await using var database = await RuntimeCheckpointDatabase.CreateAsync();
        var providerState = new RecordingProviderSessionState();
        var fixture = CreateCharacterizationFixture(
            [
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.Output,
            ],
            checkpointStore: database.Checkpoints,
            sessionStateStore: database.Sessions,
            providerSessionState: providerState
        );
        var manifest = await database.SeedAsync(fixture);
        await using var runtime = CreateRuntime(fixture, manifest);
        var messages = new List<AgwMessage>();
        await foreach (
            var message in fixture.Service.RunAsync(
                runtime,
                new TurnInput(manifest.Input) { AgentflowSnapshot = CreateSnapshot(manifest) },
                interactionHandler: null,
                TestContext.Current.CancellationToken,
                manifest.TurnId
            )
        )
        {
            messages.Add(message);
            if (MessageShape(message) == "agentflow-checkpoint")
                break;
        }
        var occurrence = Assert.Single(runtime.CheckpointOccurrenceIds);
        Assert.True(runtime.TryGetCheckpoint(occurrence, out var checkpoint));
        Assert.NotNull(checkpoint);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], ConversationContentShapes(messages));
        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await context.AgentSessionStates.CountAsync(TestContext.Current.CancellationToken));
        }
        var restored = await database.Checkpoints.PrepareInProcessResumeAsync(
            occurrence,
            manifest.Task.ProjectId,
            manifest.Task.ContextId,
            fixture.Flow.Id,
            "tester",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(manifest.AgentflowRevision, restored.RevisionSnapshot.RootRevision);
        await using var resumedRuntime = CreateRuntime(fixture, manifest);
        var initializedBeforeResume = providerState.Scopes.Count;

        var resumed = await CollectAsync(
            fixture.Service.RunAsync(
                resumedRuntime,
                new TurnInput(manifest.Input)
                {
                    AgentflowSnapshot = restored.RevisionSnapshot,
                    Resume = new TurnResume
                    {
                        Checkpoint = restored.Checkpoint,
                        CheckpointNodeIds = restored
                            .Markers.Select(marker => marker.NodeId)
                            .ToHashSet(StringComparer.Ordinal),
                    },
                },
                interactionHandler: null,
                TestContext.Current.CancellationToken,
                manifest.TurnId
            )
        );

        Assert.Equal(["done", "done"], ConversationContentShapes(resumed));
        Assert.Empty(resumedRuntime.CheckpointOccurrenceIds);
        Assert.DoesNotContain(
            providerState.Scopes.Skip(initializedBeforeResume),
            item => item.HistoryScope.EndsWith("node-0", StringComparison.Ordinal)
        );
        await using (var scope = database.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await context.AgentSessionStates.CountAsync(TestContext.Current.CancellationToken));
        }
        Assert.All(
            providerState.Scopes,
            item =>
            {
                Assert.Equal(manifest.Task.ProjectId, item.ProjectId);
                Assert.Equal(manifest.Task.ContextId, item.ContextId);
                Assert.StartsWith($"agentflow:{fixture.Flow.Id:N}:node:", item.HistoryScope);
            }
        );
        Assert.Contains(
            providerState.Scopes.Skip(initializedBeforeResume),
            item => item.HistoryScope.EndsWith("node-2", StringComparison.Ordinal)
        );
        Assert.All(fixture.Agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_CheckpointAndHumanGate_ResumesWithoutReplayingPreviousNodes()
    {
        await using var database = await RuntimeCheckpointDatabase.CreateAsync();
        var fixture = CreateCharacterizationFixture(
            [
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.HumanGate,
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.Output,
            ],
            checkpointStore: database.Checkpoints
        );
        var manifest = await database.SeedAsync(fixture);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.TurnId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], ConversationContentShapes(sink.Messages));
        var request = Assert.Single(waiting.PendingInteractions);
        var checkpointMessage = sink.Messages.Single(message => MessageShape(message) == "agentflow-checkpoint");
        Assert.Equal("node-1", checkpointMessage.AdditionalProperties!["nodeId"]);
        await PublishSecondRevisionAsync(database, fixture);

        var resumed = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.TurnId, 1, [CreateResponse(manifest, request, true)], waiting.Checkpoint),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, resumed.Status);
        Assert.Empty(resumed.PendingInteractions);
        Assert.Null(resumed.Checkpoint);
        Assert.Equal(
            ["input", "done", "agentflow-checkpoint", "done", "done"],
            ConversationContentShapes(sink.Messages)
        );
        Assert.All(fixture.Agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task RunSegment_ManifestWithoutRevisionSnapshot_FailsWithConflict()
    {
        await using var database = await RuntimeCheckpointDatabase.CreateAsync();
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            checkpointStore: database.Checkpoints
        );
        var manifest = await database.SeedAsync(fixture);
        var sink = new RecordingSegmentSink();

        var result = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest with
            {
                AgentflowDependencies = null,
            },
            new(manifest.TurnId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code.ToString(CultureInfo.InvariantCulture), result.ErrorCode);
        Assert.Empty(result.PendingInteractions);
        Assert.Empty(fixture.Agents.CreatedAgents);
    }

    [Fact]
    public async Task RunAsync_ParallelMarkers_ShareOccurrenceAndContinueBoth()
    {
        await using var database = await RuntimeCheckpointDatabase.CreateAsync();
        var fixture = CreateCharacterizationFixture(
            [
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.Output,
            ],
            checkpointStore: database.Checkpoints
        );
        fixture.Edges.Remove(fixture.Edges.Queryable.Single(edge => edge.EdgeId == "edge-1"));
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                AgentflowRevision = AgentflowTurnTestHost.FirstRevision.RootRevision,
                EdgeId = "second-marker",
                SourceNodeId = "node-0",
                TargetNodeId = "node-2",
            }
        );
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                AgentflowRevision = AgentflowTurnTestHost.FirstRevision.RootRevision,
                EdgeId = "first-output",
                SourceNodeId = "node-1",
                TargetNodeId = "node-3",
            }
        );
        var manifest = await database.SeedAsync(fixture);
        await using var runtime = CreateRuntime(fixture, manifest);

        var messages = await CollectAsync(
            fixture.Service.RunAsync(
                runtime,
                new TurnInput(manifest.Input) { AgentflowSnapshot = CreateSnapshot(manifest) },
                interactionHandler: null,
                TestContext.Current.CancellationToken
            )
        );

        var occurrenceId = Assert.Single(runtime.CheckpointOccurrenceIds);
        Assert.True(runtime.TryGetCheckpoint(occurrenceId, out var checkpoint));
        Assert.Equal(["node-1", "node-2"], checkpoint!.Markers.Select(marker => marker.NodeId));
        Assert.Equal(2, messages.Count(message => MessageShape(message) == "agentflow-checkpoint"));
    }

    [Fact]
    public async Task ResumeCheckpoint_AfterNewRevisionPublished_RunsCheckpointRevision()
    {
        await using var database = await RuntimeCheckpointDatabase.CreateAsync();
        var fixture = CreateCharacterizationFixture(
            [
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.Output,
            ],
            checkpointStore: database.Checkpoints
        );
        var manifest = await database.SeedAsync(fixture);
        await using var runtime = CreateRuntime(fixture, manifest);
        var initialMessages = new List<AgwMessage>();
        await foreach (
            var message in fixture.Service.RunAsync(
                runtime,
                new TurnInput(manifest.Input) { AgentflowSnapshot = CreateSnapshot(manifest) },
                interactionHandler: null,
                TestContext.Current.CancellationToken,
                manifest.TurnId
            )
        )
        {
            initialMessages.Add(message);
            if (MessageShape(message) == "agentflow-checkpoint")
                break;
        }
        var occurrenceId = Assert.Single(runtime.CheckpointOccurrenceIds);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], ConversationContentShapes(initialMessages));

        await PublishSecondRevisionAsync(database, fixture);
        var restored = await database.Checkpoints.PrepareInProcessResumeAsync(
            occurrenceId,
            manifest.Task.ProjectId,
            manifest.Task.ContextId,
            fixture.Flow.Id,
            "tester",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(manifest.AgentflowRevision, restored.RevisionSnapshot.RootRevision);
        await using var resumedRuntime = CreateRuntime(fixture, manifest);
        var resumedMessages = await CollectAsync(
            fixture.Service.RunAsync(
                resumedRuntime,
                new TurnInput(manifest.Input)
                {
                    AgentflowSnapshot = restored.RevisionSnapshot,
                    Resume = new TurnResume
                    {
                        Checkpoint = restored.Checkpoint,
                        CheckpointNodeIds = restored
                            .Markers.Select(marker => marker.NodeId)
                            .ToHashSet(StringComparer.Ordinal),
                    },
                },
                interactionHandler: null,
                TestContext.Current.CancellationToken,
                manifest.TurnId
            )
        );

        Assert.Contains("done", ConversationContentShapes(resumedMessages));
        Assert.DoesNotContain("agentflow-checkpoint", ConversationContentShapes(resumedMessages));
        Assert.Empty(resumedRuntime.CheckpointOccurrenceIds);
    }

    private static async Task PublishSecondRevisionAsync(
        RuntimeCheckpointDatabase database,
        CharacterizationFixture fixture
    )
    {
        const int revision = 2;
        var nodes = fixture
            .Nodes.Select(node => new AgentflowNode
            {
                AgentflowId = node.AgentflowId,
                AgentflowRevision = revision,
                NodeId = node.NodeId,
                Kind = node.NodeId == "node-2" ? AgentflowNodeKind.CheckpointMarker : node.Kind,
                RelateId = node.NodeId == "node-2" ? null : node.RelateId,
                Name = node.Name,
                PositionX = node.PositionX,
                PositionY = node.PositionY,
                RoutingStrategy = node.RoutingStrategy,
                SummaryModelProviderId = node.SummaryModelProviderId,
                Instructions = node.Instructions,
                ConfigJson = node.ConfigJson,
                CreateBy = "tester",
            })
            .ToArray();
        var edges = fixture
            .Edges.Queryable.Where(edge => edge.AgentflowRevision == AgentflowTurnTestHost.FirstRevision.RootRevision)
            .Select(edge => new AgentflowEdge
            {
                AgentflowId = edge.AgentflowId,
                AgentflowRevision = revision,
                EdgeId = edge.EdgeId,
                SourceNodeId = edge.SourceNodeId,
                TargetNodeId = edge.TargetNodeId,
                Label = edge.Label,
                ConditionJson = edge.ConditionJson,
                IsFanInBarrier = edge.IsFanInBarrier,
                IsSwitchDefault = edge.IsSwitchDefault,
                SwitchCaseOrder = edge.SwitchCaseOrder,
                ChoiceId = edge.ChoiceId,
                CreateBy = "tester",
            })
            .ToArray();

        foreach (var node in nodes)
            await fixture.NodeRepository.AddAsync(node);
        foreach (var edge in edges)
            await fixture.Edges.AddAsync(edge);

        fixture.Flow.LatestRevision = revision;
        fixture.Flow.PublishedRevision = revision;
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var persistedFlow = await context.Agentflows.SingleAsync(
            flow => flow.Id == fixture.Flow.Id,
            TestContext.Current.CancellationToken
        );
        persistedFlow.LatestRevision = revision;
        persistedFlow.PublishedRevision = revision;
        context.AgentflowRevisions.Add(
            new AgentflowRevision
            {
                AgentflowId = fixture.Flow.Id,
                Revision = revision,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        context.AgentflowNodes.AddRange(nodes);
        context.AgentflowEdges.AddRange(edges);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AgentflowRuntime CreateRuntime(CharacterizationFixture fixture, DurableExecutionManifest manifest) =>
        AgentflowRuntimeFactory.CreateRuntime(
            fixture.Flow.Id,
            manifest.Task.ToProjection(),
            new ExecutionSettings(manifest.Task.ProjectId, manifest.Task.ProjectConversationId),
            deferHumanInteractions: false
        );

    private sealed class RecordingProviderSessionState : IProviderSessionState
    {
        public List<(Guid ProjectId, string ContextId, string HistoryScope)> Scopes { get; } = [];

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId) =>
            Scopes.Add((projectId, contextId, ""));

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) => Scopes.Add((projectId, contextId, historyScope));

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            projectId = default;
            contextId = "";
            return false;
        }
    }

    private static AgentflowRevisionSnapshot CreateSnapshot(DurableExecutionManifest manifest) =>
        new(manifest.AgentflowRevision, manifest.AgentflowDependencies!);

    private static IEnumerable<string> ConversationContentShapes(IEnumerable<AgwMessage> messages) =>
        messages.Where(message => MessageShape(message) != "agentflow-node-status").Select(MessageShape);

    private sealed class RuntimeCheckpointDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Services { get; }
        public AgentflowCheckpointStore Checkpoints { get; }
        public AgentSessionStateStore Sessions { get; }

        private RuntimeCheckpointDatabase(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            Services = services;
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var applicationLock = new InMemoryApplicationLock();
            Checkpoints = new AgentflowCheckpointStore(
                scopes,
                applicationLock,
                TimeProvider.System,
                NullLogger<AgentflowCheckpointStore>.Instance
            );
            Sessions = new AgentSessionStateStore(
                scopes,
                TimeProvider.System,
                NullLogger<AgentSessionStateStore>.Instance,
                applicationLock
            );
        }

        public static async Task<RuntimeCheckpointDatabase> CreateAsync()
        {
            var connection = new SqliteConnection($"Data Source=runtime-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var services = new ServiceCollection();
            services.AddDbContext<AgwDbContext>(options =>
                options.UseSqlite(connection.ConnectionString).UseSnakeCaseNamingConvention()
            );
            services.AddScoped<IAgentsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IAgentConversationChangesWriter>(provider =>
                provider.GetRequiredService<AgwDbContext>()
            );
            services.AddScoped<IAgentflowCheckpointPersistence, AgentflowCheckpointPersistence>();
            services.AddScoped<IConversationTurnStore>(provider => new ConversationTurnStore(
                provider.GetRequiredService<AgwDbContext>(),
                TimeProvider.System
            ));
            services.AddScoped<IAgentflowRevisionSnapshotReader, AgentflowRevisionSnapshotReader>();
            services.AddScoped<IAgentSessionStatePersistence, AgentSessionStatePersistence>();
            services.AddScoped<IDurableExecutionScopeMaintenance>(provider =>
                TestDurablePersistence.Create(provider.GetRequiredService<AgwDbContext>())
            );
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new RuntimeCheckpointDatabase(connection, provider);
        }

        public async Task<DurableExecutionManifest> SeedAsync(CharacterizationFixture fixture)
        {
            var manifest = CreateManifest(fixture.Flow.Id);
            await using var scope = Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            context.Projects.Add(
                new Project
                {
                    Id = manifest.Task.ProjectId,
                    Name = "Runtime tests",
                    CreateBy = "tester",
                }
            );
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = manifest.Task.ProjectConversationId,
                    ProjectId = manifest.Task.ProjectId,
                    ContextId = manifest.Task.ContextId,
                    Title = "Runtime tests",
                    CreateBy = "tester",
                }
            );
            fixture.Flow.LatestRevision = manifest.AgentflowRevision;
            fixture.Flow.PublishedRevision = manifest.AgentflowRevision;
            context.Agentflows.Add(fixture.Flow);
            context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = fixture.Flow.Id,
                    Revision = manifest.AgentflowRevision,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            foreach (var node in fixture.Nodes)
            {
                node.AgentflowRevision = manifest.AgentflowRevision;
            }
            foreach (var edge in fixture.Edges.Queryable)
            {
                edge.AgentflowRevision = manifest.AgentflowRevision;
            }
            context.AgentflowNodes.AddRange(fixture.Nodes);
            context.AgentflowEdges.AddRange(fixture.Edges.Queryable);
            foreach (
                var agentId in fixture
                    .Nodes.Where(node => node.RelateId.HasValue)
                    .Select(node => node.RelateId!.Value)
                    .Distinct()
            )
                context.Agents.Add(
                    new Agent
                    {
                        Id = agentId,
                        Name = $"agent-{agentId:N}",
                        CreateBy = "tester",
                        Type = AgentType.System,
                    }
                );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope
                .ServiceProvider.GetRequiredService<IConversationTurnStore>()
                .AcceptAsync(
                    new AcceptConversationTurnRequest
                    {
                        TurnId = manifest.TurnId,
                        ProjectId = manifest.Task.ProjectId,
                        ContextId = manifest.Task.ContextId,
                        ConversationId = manifest.Task.ProjectConversationId,
                        Generation = 0,
                        TaskId = manifest.Task.TaskId,
                        TargetId = manifest.AgentId,
                        TargetType = ConversationTurnTargetType.Agentflow,
                        AgentflowRevision = manifest.AgentflowRevision,
                        AgentflowDependencies = manifest
                            .AgentflowDependencies!.Select(dependency => new AgentflowDependencyRevisionDto(
                                dependency.ParentAgentflowId,
                                dependency.ParentRevision,
                                dependency.ParentNodeId,
                                dependency.BlockMemberId,
                                dependency.TargetAgentflowId,
                                dependency.TargetRevision
                            ))
                            .ToArray(),
                    },
                    TestContext.Current.CancellationToken
                );
            return manifest;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
