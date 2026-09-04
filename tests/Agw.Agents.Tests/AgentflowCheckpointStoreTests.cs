using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class AgentflowCheckpointStoreTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-id")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task InProcessOccurrences_RepeatedNode_RestoreExactBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);

        var first = await store.RecordAsync(
            Guid.CreateVersion7(),
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: false,
            fingerprint!,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Review saved" },
            cancellationToken
        );
        Assert.NotNull(first);

        await database.AppendHistoryAsync(fixture, sequence: 2, "after first");
        var second = await store.RecordAsync(
            Guid.CreateVersion7(),
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: false,
            fingerprint!,
            CreateCheckpoint("checkpoint-2"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Review saved" },
            cancellationToken
        );
        Assert.NotNull(second);
        Assert.NotEqual(first.Snapshot.OccurrenceId, second.Snapshot.OccurrenceId);

        var availability = await store.ListAsync(
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            new HashSet<Guid> { first.Snapshot.OccurrenceId, second.Snapshot.OccurrenceId },
            cancellationToken
        );
        Assert.Equal(2, availability.Count);
        Assert.All(availability, checkpoint => Assert.True(checkpoint.Available));

        var restored = await store.PrepareInProcessResumeAsync(
            first.Snapshot.OccurrenceId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );

        Assert.Equal(first.Snapshot.BoundarySequence, restored.BoundarySequence);
        await using var context = database.CreateContext();
        Assert.Equal(
            [0L, first.Snapshot.BoundarySequence],
            await context
                .ProjectConversationChatHistories.OrderBy(item => item.ConversationSequence)
                .Select(item => item.ConversationSequence!.Value)
                .ToArrayAsync(cancellationToken)
        );
        var remaining = await context.AgentflowCheckpoints.SingleAsync(cancellationToken);
        Assert.Equal(first.Snapshot.OccurrenceId, remaining.Id);
    }

    [Fact]
    public async Task Record_ParallelMarkers_ShareOccurrenceAndBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);

        var recorded = await store.RecordAsync(
            Guid.CreateVersion7(),
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: false,
            fingerprint!,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-a"] = "Review A", ["checkpoint-b"] = "Review B" },
            cancellationToken
        );

        Assert.NotNull(recorded);
        Assert.Equal(2, recorded.Messages.Count);
        Assert.Equal(2, recorded.Snapshot.Markers.Count);
        Assert.All(
            recorded.Messages,
            message =>
                Assert.Equal(
                    recorded.Snapshot.OccurrenceId.ToString("D"),
                    message.AdditionalProperties!["checkpointOccurrenceId"]
                )
        );

        await using var context = database.CreateContext();
        var checkpoint = await context.AgentflowCheckpoints.SingleAsync(cancellationToken);
        Assert.Equal(recorded.Snapshot.OccurrenceId, checkpoint.Id);
        Assert.Equal(2, checkpoint.BoundarySequence);
        Assert.Equal("user-id", checkpoint.UserId);
        Assert.Equal("user-id", checkpoint.CreateBy);
        Assert.Equal("user-id", checkpoint.UpdateBy);
        var checkpointMessages = await context
            .ProjectConversationChatHistories.Where(item => item.ConversationSequence > 0)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, checkpointMessages.Length);
        var checkpointSequences = await context
            .ProjectConversationChatHistories.Where(item => item.ConversationSequence > 0)
            .OrderBy(item => item.ConversationSequence)
            .Select(item => item.ConversationSequence!.Value)
            .ToArrayAsync(cancellationToken);
        Assert.Equal([1L, 2L], checkpointSequences);
    }

    [Fact]
    public async Task RecordAsync_MissingConversation_ReturnsNullWithoutPersistence()
    {
        // Arrange
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);

        // Act
        var recorded = await store.RecordAsync(
            Guid.CreateVersion7(),
            fixture.ProjectId,
            Guid.CreateVersion7(),
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: false,
            fingerprint!,
            CreateCheckpoint("missing-conversation"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );

        // Assert
        Assert.Null(recorded);
        await using var context = database.CreateContext();
        Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
        Assert.Empty(await context.AgentflowCheckpoints.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task RecordAsync_CheckpointInsertFails_RollsBackConversationHistory()
    {
        // Arrange
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);
        await database.RejectCheckpointInsertsAsync(cancellationToken);

        // Act
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.RecordAsync(
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ConversationId,
                fixture.ContextId,
                fixture.TaskId,
                fixture.AgentflowId,
                "user-id",
                isDurable: false,
                fingerprint!,
                CreateCheckpoint("rejected-checkpoint"),
                new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
                cancellationToken
            )
        );

        // Assert
        await using var context = database.CreateContext();
        Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
        Assert.Empty(await context.AgentflowCheckpoints.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Resume_DefinitionChanged_DoesNotDeleteHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);
        var recorded = await store.RecordAsync(
            Guid.CreateVersion7(),
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: false,
            fingerprint!,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "must remain");
        await database.ChangeDefinitionAsync(fixture.AgentflowId);

        await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareInProcessResumeAsync(
                recorded.Snapshot.OccurrenceId,
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                cancellationToken
            )
        );

        await using var context = database.CreateContext();
        Assert.Equal(3, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task DistributedResume_RetryIsIdempotent_AndAllowsLaterBranch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceExecutionId = Guid.CreateVersion7();
        await database.AddDurableExecutionAsync(fixture, sourceExecutionId, DurableExecutionStatus.Completed);
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);
        var recorded = await store.RecordAsync(
            sourceExecutionId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: true,
            fingerprint!,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "old branch");
        var resumeExecutionId = Guid.CreateVersion7();

        await store.PrepareDistributedResumeAsync(
            recorded.Snapshot.OccurrenceId,
            resumeExecutionId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );
        await store.PrepareDistributedResumeAsync(
            recorded.Snapshot.OccurrenceId,
            resumeExecutionId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );

        await using (var context = database.CreateContext())
        {
            var branch = await context.DurableExecutions.SingleAsync(
                item => item.Id == resumeExecutionId,
                cancellationToken
            );
            var branchManifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                branch.ManifestJson,
                "resume branch manifest"
            );
            Assert.Equal(DurableExecutionStatus.Resuming, branch.Status);
            Assert.Equal(1, branch.SegmentIndex);
            Assert.Equal("user-id", branch.UserId);
            Assert.Equal("user-id", branch.CreateBy);
            Assert.Equal("user-id", branch.UpdateBy);
            Assert.Equal(recorded.Snapshot.OccurrenceId, branchManifest.ResumeCheckpointOccurrenceId);
            Assert.Equal(["checkpoint-node"], branchManifest.ResumeCheckpointNodeIds);
            Assert.Equal(2, await context.DurableExecutions.CountAsync(cancellationToken));
            Assert.Equal(2, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
            branch.Status = DurableExecutionStatus.Completed;
            await context.SaveChangesAsync(cancellationToken);
        }

        await database.AppendHistoryAsync(fixture, sequence: 2, "second old branch");
        var laterExecutionId = Guid.CreateVersion7();
        await store.PrepareDistributedResumeAsync(
            recorded.Snapshot.OccurrenceId,
            laterExecutionId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );

        await using var finalContext = database.CreateContext();
        Assert.Equal(3, await finalContext.DurableExecutions.CountAsync(cancellationToken));
        Assert.Equal(2, await finalContext.ProjectConversationChatHistories.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task DistributedResume_SourceStillRunning_DoesNotDeleteHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceExecutionId = Guid.CreateVersion7();
        await database.AddDurableExecutionAsync(fixture, sourceExecutionId, DurableExecutionStatus.Running);
        var fingerprint = await store.GetDefinitionFingerprintAsync(fixture.AgentflowId, cancellationToken);
        var recorded = await store.RecordAsync(
            sourceExecutionId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            "user-id",
            isDurable: true,
            fingerprint!,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "must remain");

        await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareDistributedResumeAsync(
                recorded.Snapshot.OccurrenceId,
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                cancellationToken
            )
        );

        await using var context = database.CreateContext();
        Assert.Equal(3, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.DurableExecutions.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(cancellationToken));
    }

    private static DurableAgentflowCheckpoint CreateCheckpoint(string checkpointId) =>
        new()
        {
            SessionId = "session-1",
            CheckpointId = checkpointId,
            Payload = JsonSerializer.SerializeToElement(new { checkpointId }),
        };

    private sealed record Fixture(Guid ProjectId, Guid ConversationId, string ContextId, Guid TaskId, Guid AgentflowId);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _serviceProvider;

        private TestDatabase(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            ServiceProvider serviceProvider
        )
        {
            _connection = connection;
            _options = options;
            _serviceProvider = serviceProvider;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using (var context = new AgwDbContext(options))
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AgwDbContext(options));
            services.AddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IAgentsDbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IAgentflowCheckpointPersistence, AgentflowCheckpointPersistence>();
            var serviceProvider = services.BuildServiceProvider();
            return new TestDatabase(connection, options, serviceProvider);
        }

        public AgwDbContext CreateContext() => new(_options);

        public AgentflowCheckpointStore CreateStore() =>
            new(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new InMemoryApplicationLock(),
                TimeProvider.System
            );

        public async Task<Fixture> SeedAsync()
        {
            var now = TimeProvider.System.GetUtcNow();
            var fixture = new Fixture(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "context-1",
                Guid.CreateVersion7(),
                Guid.CreateVersion7()
            );
            await using var context = CreateContext();
            context.Projects.Add(
                new Project
                {
                    Id = fixture.ProjectId,
                    Name = "Project",
                    Workspace = "/tmp",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = fixture.ConversationId,
                    ProjectId = fixture.ProjectId,
                    ContextId = fixture.ContextId,
                    Title = "Conversation",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = fixture.AgentflowId,
                    Name = "Flow",
                    SystemPrompt = "original",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = fixture.AgentflowId,
                    NodeId = "checkpoint-node",
                    Kind = AgentflowNodeKind.CheckpointMarker,
                    Name = "Saved",
                    ConfigJson = "{\"checkpointName\":\"Saved\"}",
                    CreateTime = now,
                }
            );
            context.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = fixture.ConversationId,
                    TaskId = fixture.TaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = 0,
                    ConversationPayload = "{}",
                    CreateTime = now,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public async Task AppendHistoryAsync(Fixture fixture, long sequence, string text)
        {
            await using var context = CreateContext();
            context.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = fixture.ConversationId,
                    TaskId = fixture.TaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = sequence,
                    ConversationPayload = text,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task ChangeDefinitionAsync(Guid agentflowId)
        {
            await using var context = CreateContext();
            var agentflow = await context.Agentflows.SingleAsync(
                item => item.Id == agentflowId,
                TestContext.Current.CancellationToken
            );
            agentflow.SystemPrompt = "changed";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task RejectCheckpointInsertsAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_agentflow_checkpoint_insert
                BEFORE INSERT ON agentflow_checkpoint
                BEGIN
                    SELECT RAISE(ABORT, 'checkpoint insert rejected');
                END;
                """,
                cancellationToken
            );
        }

        public async Task AddDurableExecutionAsync(Fixture fixture, Guid executionId, DurableExecutionStatus status)
        {
            var manifest = new DurableExecutionManifest
            {
                ExecutionId = executionId,
                UserId = "user-id",
                AgentId = fixture.AgentflowId,
                AgentType = AgentRuntimeType.Agentflow,
                Input = new AgwUserInput { Contents = [] },
                Task = new DurableProjectTaskSnapshot
                {
                    TaskId = fixture.TaskId,
                    ProjectConversationId = fixture.ConversationId,
                    ProjectId = fixture.ProjectId,
                    ContextId = fixture.ContextId,
                },
                Settings = DurableExecutionSettings.FromSettings(
                    ExecutionSettings.FromCommand(new SettingCommand(fixture.ProjectId, contextId: fixture.ContextId))
                ),
            };
            await using var context = CreateContext();
            context.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = executionId,
                    UserId = "user-id",
                    ManifestJson = DurableExecutionJson.Serialize(manifest),
                    Status = status,
                    StateChangedAt = TimeProvider.System.GetUtcNow(),
                    StateVersion = Guid.CreateVersion7(),
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
