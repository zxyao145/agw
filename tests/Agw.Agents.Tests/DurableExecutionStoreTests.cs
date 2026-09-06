using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionStoreTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-id")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task RegisterAsync_SameExecutionAndManifest_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var input = CreateInput("hello");
        var task = CreateTask();
        var settings = CreateSettings(task.ProjectId, task.ContextId);

        var first = await store.RegisterAsync(
            executionId,
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            input,
            task,
            settings,
            TestContext.Current.CancellationToken
        );
        var second = await store.RegisterAsync(
            executionId,
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            input,
            task,
            settings,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(executionId, first.Manifest.ExecutionId);
        Assert.Equal(executionId, second.Manifest.ExecutionId);
        Assert.Equal(DurableExecutionStatus.Queued, first.Status);
        Assert.Equal(DurableExecutionStatus.Queued, second.Status);
        Assert.Equal(1, await database.Context.DurableExecutions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterAsync_UserIdPersistsAndLegacyManifestFallsBackToAdmin()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var task = CreateTask();
        var snapshot = await store.RegisterAsync(
            Guid.CreateVersion7(),
            "user-id",
            Guid.CreateVersion7(),
            AgentRuntimeType.Agent,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("user-id", snapshot.Manifest.ResolveUserId());
        var record = await database.Context.DurableExecutions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("user-id", record.UserId);
        Assert.Equal(task.ProjectId, record.ProjectId);
        Assert.Equal(task.ProjectConversationId, record.ProjectConversationId);
        Assert.True(record.ScopeBackfilled);
        Assert.Equal("user-id", record.CreateBy);
        Assert.Equal("user-id", record.UpdateBy);
        Assert.Equal(Constants.AdminUserId, CreateManifest().ResolveUserId());
    }

    [Fact]
    public async Task Coordinator_StartAsync_PersistsExplicitUserIdInManifest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        await using var serviceProvider = services.BuildServiceProvider();
        var coordinator = new DurableExecutionCoordinator(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new InMemoryApplicationLock(),
            new RecordingExecutionEventStream(),
            TimeProvider.System,
            Options.Create(new ExecutionRuntimeOptions()),
            NullLogger<DurableExecutionCoordinator>.Instance
        );
        var executionId = Guid.CreateVersion7();
        var task = CreateTask();
        await coordinator.StartAsync(
            executionId,
            "user-id",
            new ExecCommand(AgentRuntimeType.Agent, CreateInput("hello"))
            {
                AgentId = Guid.CreateVersion7(),
                ConversationId = Guid.CreateVersion7(),
            },
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            TestContext.Current.CancellationToken
        );

        var snapshot = await store.GetAsync(executionId, TestContext.Current.CancellationToken);
        Assert.Equal("user-id", snapshot.Manifest.ResolveUserId());
    }

    [Fact]
    public async Task RegisterAsync_SameExecutionWithDifferentManifest_ThrowsConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var task = CreateTask();
        var settings = CreateSettings(task.ProjectId, task.ContextId);

        await store.RegisterAsync(
            executionId,
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            CreateInput("first"),
            task,
            settings,
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            store.RegisterAsync(
                executionId,
                "user-id",
                agentId,
                AgentRuntimeType.Agent,
                CreateInput("different"),
                task,
                settings,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public async Task GetAuthorizedAsync_DifferentUserId_ReturnsNotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            store.GetAuthorizedAsync(executionId, "another-user", TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.DurableExecutionNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task GetAuthorizedOutcomeAsync_RunningState_DoesNotDeserializeManifest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        var record = await database.Context.DurableExecutions.SingleAsync(TestContext.Current.CancellationToken);
        record.ManifestJson = "not-a-valid-manifest";
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outcome = await store.GetAuthorizedOutcomeAsync(
            executionId,
            "user-id",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(executionId, outcome.ExecutionId);
        Assert.Equal(DurableExecutionStatus.Queued, outcome.Status);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task GetAuthorizedOutcomeAsync_FailedState_LoadsDecryptedError()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Failed,
                ErrorMessage = "boom",
            },
            TestContext.Current.CancellationToken
        );

        var outcome = await store.GetAuthorizedOutcomeAsync(
            executionId,
            "user-id",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionStatus.Failed, outcome.Status);
        Assert.Equal("boom", outcome.ErrorMessage);
    }

    [Fact]
    public async Task SegmentState_WaitingAndResponse_RestoresNextSegmentInput()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        var running = await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(running);
        var checkpoint = new DurableAgentflowCheckpoint
        {
            SessionId = "workflow-session",
            CheckpointId = "checkpoint-1",
            Payload = JsonSerializer.SerializeToElement(new { step = 1 }),
        };

        var waiting = await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [CreateInteraction("request-1")],
                Checkpoint = checkpoint,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(1, waiting.SegmentIndex);
        Assert.Single(waiting.GetUnansweredInteractions());

        var resuming = await store.SubmitHumanResponseAsync(
            new SubmitDurableHumanResponseRequest(
                executionId,
                "request-1",
                Approved: true,
                ResponseData: JsonSerializer.SerializeToElement(new { answer = "blue" })
            ),
            "user-id",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionStatus.Resuming, resuming.Status);
        Assert.Empty(resuming.GetUnansweredInteractions());

        var resumed = await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(resumed);
        var input = resumed.CreateSegmentInput();
        Assert.Equal(1, input.SegmentIndex);
        Assert.Equal("checkpoint-1", input.Checkpoint?.CheckpointId);
        var resolved = Assert.Single(input.ResolvedInteractions);
        Assert.Equal("request-1", resolved.Request.RequestId);
        Assert.Equal("blue", resolved.Response.ResponseData?.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task CheckpointStore_SeededFromActivityResult_RestoresPayload()
    {
        var sessionId = $"durable-{Guid.CreateVersion7():N}";
        var firstStore = new DurableAgentflowCheckpointStore();
        var first = await firstStore.CreateCheckpointAsync(
            sessionId,
            JsonSerializer.SerializeToElement(new { step = 1 })
        );
        var second = await firstStore.CreateCheckpointAsync(
            sessionId,
            JsonSerializer.SerializeToElement(new { step = 2 }),
            first
        );

        var restoredStore = new DurableAgentflowCheckpointStore(firstStore.Latest);
        var index = (await restoredStore.RetrieveIndexAsync(sessionId)).ToArray();
        var payload = await restoredStore.RetrieveCheckpointAsync(sessionId, second);

        Assert.Single(index);
        Assert.Equal(second.CheckpointId, index[0].CheckpointId);
        Assert.Equal(2, payload.GetProperty("step").GetInt32());
        Assert.Equal(first.CheckpointId, restoredStore.Latest?.ParentCheckpointId);
    }

    [Fact]
    public async Task InterruptRunningSegment_ResultCannotOverwriteInterruptedState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        var running = await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(running);

        var interrupted = await store.RequestInterruptAsync(
            executionId,
            "user-id",
            TestContext.Current.CancellationToken
        );
        var persisted = await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Completed,
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(interrupted);
        Assert.Equal(DurableExecutionStatus.Interrupted, persisted.Status);
        Assert.Equal(DurableExecutionStatus.Interrupted, DurableExecutionCoordinator.ToStatus(persisted).Status);
        Assert.Equal(
            persisted.Manifest.Input.MessageId,
            DurableExecutionCoordinator.ToStatus(persisted).StreamingScopeId
        );
    }

    [Fact]
    public async Task SubmitHumanResponseAsync_SameResponse_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [CreateInteraction("request-1")],
            },
            TestContext.Current.CancellationToken
        );
        var request = new SubmitDurableHumanResponseRequest(
            executionId,
            "request-1",
            Approved: true,
            ResponseData: JsonSerializer.SerializeToElement(new { answer = "blue" })
        );

        var first = await store.SubmitHumanResponseAsync(request, "user-id", TestContext.Current.CancellationToken);
        var second = await store.SubmitHumanResponseAsync(request, "user-id", TestContext.Current.CancellationToken);

        Assert.Equal(DurableExecutionStatus.Resuming, first.Status);
        Assert.Equal(DurableExecutionStatus.Resuming, second.Status);
        Assert.Single(second.Responses);
    }

    [Fact]
    public async Task GetRunnableExecutionIdsAsync_QueuedExecution_IsReturned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);

        var candidates = await store.GetRunnableExecutionIdsAsync(
            TimeProvider.System.GetUtcNow(),
            limit: 10,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(executionId, candidates);
    }

    [Fact]
    public async Task GetRunnableExecutionIdsAsync_WaitingExecution_IsNotReturned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [CreateInteraction("request-1")],
            },
            TestContext.Current.CancellationToken
        );

        var candidates = await store.GetRunnableExecutionIdsAsync(
            TimeProvider.System.GetUtcNow(),
            limit: 10,
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain(executionId, candidates);
    }

    [Fact]
    public async Task GetRunnableExecutionIdsAsync_StaleRunningExecution_IsReturned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        var running = await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(running);

        var candidates = await store.GetRunnableExecutionIdsAsync(
            TimeProvider.System.GetUtcNow().AddMinutes(1),
            limit: 10,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(executionId, candidates);
    }

    [Fact]
    public async Task RequestInterruptAsync_TerminalExecution_ReturnsFalse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(store);
        await store.TryBeginSegmentAsync(
            executionId,
            TimeProvider.System.GetUtcNow(),
            TestContext.Current.CancellationToken
        );
        await store.SaveSegmentResultAsync(
            new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Completed,
            },
            TestContext.Current.CancellationToken
        );

        var interrupted = await store.RequestInterruptAsync(
            executionId,
            "user-id",
            TestContext.Current.CancellationToken
        );

        Assert.False(interrupted);
    }

    [Fact]
    public void CreateSegmentInput_MissingResponse_ThrowsConflict()
    {
        var snapshot = new DurableExecutionSnapshot
        {
            Manifest = CreateManifest(),
            Status = DurableExecutionStatus.Resuming,
            SegmentIndex = 1,
            PendingInteractions = [CreateInteraction("request-1")],
        };

        var exception = Assert.Throws<AgwException>(snapshot.CreateSegmentInput);

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResolvedHumanInteractionChannel_RequestAsync_ReplaysMatchedResponse(
        bool approved,
        bool expectedCancelled
    )
    {
        var responseData = JsonSerializer.SerializeToElement(new { answer = "blue" });
        var channel = new ResolvedHumanInteractionChannel([
            new DurableResolvedInteraction(
                CreateInteraction("request-1"),
                new DurableHumanResponseEnvelope
                {
                    ExecutionId = Guid.CreateVersion7(),
                    RequestId = "request-1",
                    Approved = approved,
                    ResponseData = responseData,
                }
            ),
        ]);
        var request = new HumanInteractionRequest(
            "runtime-request",
            "ask_user_question",
            "Choose a color",
            JsonSerializer.SerializeToElement(new { question = "Color?" })
        )
        {
            ToolName = "ask_user_question",
            CallId = "call-request-1",
        };

        var response = await channel.RequestAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("runtime-request", response.RequestId);
        Assert.Equal(expectedCancelled, response.Cancelled);
        Assert.Equal("blue", response.ResponseData?.GetProperty("answer").GetString());
    }

    [Fact]
    public void DurableHumanInteractionMapper_ToMessage_RecreatesQuestionPresentation()
    {
        var message = DurableHumanInteractionMapper.ToMessage(
            CreateInteraction("request-1"),
            Guid.CreateVersion7(),
            "message-1"
        );

        Assert.Equal("human-interaction-request", message.AdditionalProperties?["type"]);
        Assert.Equal("request-1", message.AdditionalProperties?["requestId"]);
        Assert.Equal("call-request-1", message.AdditionalProperties?["callId"]);
        Assert.Equal("message-1", message.AdditionalProperties?["streamingScopeId"]);
        var payload = Assert.IsType<JsonElement>(message.AdditionalProperties?["payload"]);
        Assert.Equal("Color?", payload.GetProperty("questions")[0].GetProperty("question").GetString());
    }

    [Fact]
    public void TurnMessageFactory_CreateStarted_PreservesDurableRenderingScope()
    {
        var executionId = Guid.CreateVersion7();

        var message = TurnMessageFactory.CreateStarted(executionId, "message-1");

        Assert.Equal("turn-start", message.AdditionalProperties?["type"]);
        Assert.Equal(executionId.ToString("D"), message.AdditionalProperties?["executionId"]);
        Assert.Equal("message-1", message.AdditionalProperties?["streamingScopeId"]);
    }

    [Fact]
    public void RedisExecutionEventStream_CreateStreamId_ReservesTerminalAfterRetryOutput()
    {
        Assert.Equal("2-3", RedisExecutionEventStream.CreateStreamId(1, 3, terminal: false));
        Assert.Equal("2-18446744073709551615", RedisExecutionEventStream.CreateStreamId(1, 0, terminal: true));
    }

    [Fact]
    public async Task PostgresExecutionEventStream_AppendReadAndCursor_AreDurableAndIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var serviceProvider = database.CreateServiceProvider();
        var stream = new PostgresExecutionEventStream(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(
                new ExecutionRuntimeOptions
                {
                    Provider = ExecutionProvider.Distributed,
                    Distributed = new DistributedExecutionOptions
                    {
                        EventStream = new ExecutionEventStreamOptions
                        {
                            Provider = ExecutionEventStreamProvider.Postgres,
                            ReadBatchSize = 2,
                        },
                    },
                }
            )
        );
        var executionId = Guid.CreateVersion7();
        await RegisterExecutionAsync(database.CreateStore(), executionId);
        var firstMessage = TurnMessageFactory.CreateStarted(executionId);
        var secondMessage = TurnMessageFactory.CreateFinished("failed", executionId);
        var thirdMessage = TurnMessageFactory.CreateFinished("completed", executionId);

        await stream.AppendAsync(
            executionId,
            segmentIndex: 0,
            sequence: 0,
            firstMessage,
            TestContext.Current.CancellationToken
        );
        await stream.AppendAsync(
            executionId,
            segmentIndex: 0,
            sequence: 1,
            secondMessage,
            TestContext.Current.CancellationToken
        );
        await stream.AppendAsync(
            executionId,
            segmentIndex: 1,
            sequence: 0,
            thirdMessage,
            TestContext.Current.CancellationToken
        );
        await stream.AppendAsync(
            executionId,
            segmentIndex: 0,
            sequence: 0,
            firstMessage,
            TestContext.Current.CancellationToken
        );

        var firstBatch = await stream.ReadAsync(executionId, afterCursor: null, TestContext.Current.CancellationToken);
        var secondBatch = await stream.ReadAsync(
            executionId,
            firstBatch[^1].Cursor,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["1-0", "1-1"], firstBatch.Select(item => item.Cursor));
        Assert.Equal("2-0", Assert.Single(secondBatch).Cursor);
        Assert.All(
            firstBatch.Concat(secondBatch),
            item => Assert.Equal(executionId.ToString("D"), item.Message.AdditionalProperties?["executionId"])
        );
        Assert.Equal(
            3,
            await database.Context.DurableExecutionEvents.CountAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task PostgresExecutionEventStream_SamePositionWithDifferentMessage_KeepsFirstEntry()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var serviceProvider = database.CreateServiceProvider();
        var stream = new PostgresExecutionEventStream(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExecutionRuntimeOptions())
        );
        var executionId = Guid.CreateVersion7();
        await RegisterExecutionAsync(database.CreateStore(), executionId);

        await stream.AppendAsync(
            executionId,
            segmentIndex: 0,
            sequence: 0,
            TurnMessageFactory.CreateStarted(executionId),
            TestContext.Current.CancellationToken
        );
        await stream.AppendAsync(
            executionId,
            segmentIndex: 0,
            sequence: 0,
            TurnMessageFactory.CreateFinished("completed", executionId),
            TestContext.Current.CancellationToken
        );
        var entry = Assert.Single(
            await stream.ReadAsync(executionId, afterCursor: null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("turn-start", entry.Message.AdditionalProperties?["type"]?.ToString());
    }

    [Fact]
    public async Task ExecutionStreamMessageSink_OnlyTerminalControlMessage_IsPublishedExplicitly()
    {
        var stream = new RecordingExecutionEventStream();
        var executionId = Guid.CreateVersion7();
        var sink = new ExecutionStreamMessageSink(stream, executionId, segmentIndex: 2, NullLogger.Instance);
        var interaction = DurableHumanInteractionMapper.ToMessage(CreateInteraction("request-1"));

        await sink.WriteAsync(interaction, TestContext.Current.CancellationToken);
        await sink.WriteAsync(TurnMessageFactory.CreateFinished(), TestContext.Current.CancellationToken);

        Assert.Empty(stream.Appends);

        await sink.WriteTerminalAsync("completed", TestContext.Current.CancellationToken);

        var append = Assert.Single(stream.Appends);
        Assert.Equal(int.MaxValue, append.Sequence);
        Assert.Equal("turn-finished", append.Message.AdditionalProperties?["type"]);
    }

    private static async Task<Guid> RegisterExecutionAsync(DurableExecutionStore store, Guid? executionId = null)
    {
        var resolvedExecutionId = executionId ?? Guid.CreateVersion7();
        var task = CreateTask();
        await store.RegisterAsync(
            resolvedExecutionId,
            "user-id",
            Guid.CreateVersion7(),
            AgentRuntimeType.Agent,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            TestContext.Current.CancellationToken
        );
        return resolvedExecutionId;
    }

    private static DurableExecutionManifest CreateManifest()
    {
        var task = CreateTask();
        return new DurableExecutionManifest
        {
            ExecutionId = Guid.CreateVersion7(),
            AgentId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            Input = CreateInput("hello"),
            Task = DurableProjectTaskSnapshot.FromProjection(task),
            Settings = DurableExecutionSettings.FromSettings(CreateSettings(task.ProjectId, task.ContextId)),
        };
    }

    private static DurableHumanInteractionSnapshot CreateInteraction(string requestId) =>
        new()
        {
            RequestId = requestId,
            Kind = "questions",
            NodeId = "standalone",
            NodeName = "Agent",
            ToolName = "ask_user_question",
            CallId = $"call-{requestId}",
            Prompt = "Choose a color",
            Payload = JsonSerializer.SerializeToElement(new { questions = new[] { new { question = "Color?" } } }),
        };

    private static AgwUserInput CreateInput(string content) =>
        new() { MessageId = "message-1", Contents = [new AgwTextContent { Content = content }] };

    private static AgentExecutionTask CreateTask() =>
        new()
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context-1",
            Title = "Durable test",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ExecutionSettings CreateSettings(Guid projectId, string contextId) =>
        ExecutionSettings.FromCommand(new SettingCommand(projectId, contextId: contextId));

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;

        private TestDatabase(SqliteConnection connection, DbContextOptions<AgwDbContext> options, AgwDbContext context)
        {
            _connection = connection;
            _options = options;
            Context = context;
        }

        public AgwDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new TestDatabase(connection, options, context);
        }

        public DurableExecutionStore CreateStore() =>
            new(
                Context,
                TimeProvider.System,
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                TestDurablePersistence.Create(Context)
            );

        public ServiceProvider CreateServiceProvider()
        {
            var services = new ServiceCollection();
            services.AddScoped<IAgentsDbContext>(_ => new AgwDbContext(_options));
            return services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingExecutionEventStream : IExecutionEventStream
    {
        public List<(Guid ExecutionId, int SegmentIndex, int Sequence, AgwMessage Message)> Appends { get; } = [];

        public ValueTask AppendAsync(
            Guid executionId,
            int segmentIndex,
            int sequence,
            AgwMessage message,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Appends.Add((executionId, segmentIndex, sequence, message));
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionStreamEntry>> ReadAsync(
            Guid executionId,
            string? afterCursor,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ExecutionStreamEntry>>([]);
        }
    }
}
