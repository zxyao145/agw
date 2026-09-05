using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task ExecuteStreamingWithPermissionStateAsync_RealCheckpoint_ResumesAndPersistsNodeSessions()
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
        var checkpointState = new AgentflowCheckpointRuntimeState();
        var permissionState = new PermissionModeState(null);
        var messages = new List<AgwMessage>();
        await foreach (
            var message in fixture.Service.ExecuteStreamingWithPermissionStateAsync(
                fixture.Flow.Id,
                manifest.Input,
                TestContext.Current.CancellationToken,
                manifest.Task.ProjectId,
                manifest.Task.ContextId,
                manifest.Task.TaskId,
                null,
                null,
                manifest.Task.ProjectConversationId,
                permissionState,
                manifest.ExecutionId,
                checkpointState,
                null
            )
        )
        {
            messages.Add(message);
            if (MessageShape(message) == "agentflow-checkpoint")
                break;
        }
        var occurrence = Assert.Single(checkpointState.OccurrenceIds);
        Assert.True(checkpointState.TryGet(occurrence, out var checkpoint));
        Assert.NotNull(checkpoint);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], messages.Select(MessageShape));
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
        var resumedState = new AgentflowCheckpointRuntimeState();
        var initializedBeforeResume = providerState.Scopes.Count;

        var resumed = await CollectAsync(
            fixture.Service.ExecuteStreamingWithPermissionStateAsync(
                fixture.Flow.Id,
                manifest.Input,
                TestContext.Current.CancellationToken,
                manifest.Task.ProjectId,
                manifest.Task.ContextId,
                manifest.Task.TaskId,
                null,
                null,
                manifest.Task.ProjectConversationId,
                permissionState,
                Guid.CreateVersion7(),
                resumedState,
                restored
            )
        );

        Assert.Equal(["done", "done", "turn-finished"], resumed.Select(MessageShape));
        Assert.Empty(resumedState.OccurrenceIds);
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
        var waiting = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], sink.Messages.Select(MessageShape));
        var request = Assert.Single(waiting.PendingInteractions);
        var checkpointMessage = sink.Messages.Single(message => MessageShape(message) == "agentflow-checkpoint");
        Assert.Equal("node-1", checkpointMessage.AdditionalProperties!["checkpointNodeId"]);

        var resumed = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 1, [CreateResponse(manifest, request, true)], waiting.Checkpoint),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, resumed.Status);
        Assert.Empty(resumed.PendingInteractions);
        Assert.Null(resumed.Checkpoint);
        Assert.Equal(["input", "done", "agentflow-checkpoint"], sink.Messages.Select(MessageShape));
        Assert.All(fixture.Agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task ExecuteStreamingWithPermissionStateAsync_ParallelMarkers_ShareOccurrenceAndContinueBoth()
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
                EdgeId = "second-marker",
                SourceNodeId = "node-0",
                TargetNodeId = "node-2",
            }
        );
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                EdgeId = "first-output",
                SourceNodeId = "node-1",
                TargetNodeId = "node-3",
            }
        );
        var manifest = await database.SeedAsync(fixture);
        var state = new AgentflowCheckpointRuntimeState();

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingWithPermissionStateAsync(
                fixture.Flow.Id,
                manifest.Input,
                TestContext.Current.CancellationToken,
                manifest.Task.ProjectId,
                manifest.Task.ContextId,
                manifest.Task.TaskId,
                null,
                null,
                manifest.Task.ProjectConversationId,
                new PermissionModeState(null),
                manifest.ExecutionId,
                state,
                null
            )
        );

        var occurrenceId = Assert.Single(state.OccurrenceIds);
        Assert.True(state.TryGet(occurrenceId, out var checkpoint));
        Assert.Equal(["node-1", "node-2"], checkpoint!.Markers.Select(marker => marker.NodeId));
        Assert.Equal(2, messages.Count(message => MessageShape(message) == "agentflow-checkpoint"));
        Assert.Equal("turn-finished", MessageShape(messages[^1]));
    }

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
            Checkpoints = new AgentflowCheckpointStore(scopes, applicationLock, TimeProvider.System);
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
            services.AddScoped<IAgentflowCheckpointPersistence, AgentflowCheckpointPersistence>();
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
            context.Agentflows.Add(fixture.Flow);
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
            return manifest;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
