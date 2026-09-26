using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Inbound.SignalR;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Application;
using Agw.Files.Abstracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecutionConnectionTests
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionQueries_WhileRuntimeQueryIsPending_UseIndependentDbContext(bool changePermission)
    {
        // Arrange: real SQLite queries, with the runtime query paused inside EF's concurrency guard.
        var ct = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var database = new SqliteConnection(connectionString);
        await database.OpenAsync(ct);
        var blocker = new BlockingQueryInterceptor();
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
        };
        var command = CreateExecCommand();
        var factories = new List<DatabaseAgentRuntimeFactory>();
        var kit = new InProcessCoordinatorTestKit();
        var services = new ServiceCollection();
        services.AddOptions<ExecutionRuntimeOptions>();
        services.AddDbContext<AgwDbContext>(options => options.UseSqlite(connectionString).AddInterceptors(blocker));
        services.AddScoped<IAgentsDbContext>(sp => sp.GetRequiredService<AgwDbContext>());
        services.AddScoped<IProjectsDbContext>(sp => sp.GetRequiredService<AgwDbContext>());
        services.AddScoped<IUserInfoService, UserInfoService>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<IProjectDefaultResolver, ProjectDefaultResolver>();
        services.AddScoped<ExecutionPermissionService>();
        services.AddSingleton<IApplicationLock>(InMemoryApplicationLock.Shared);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentflowCheckpointStore>();
        services.AddSingleton<IProjectTaskFacade>(provider => new FakeProjectTaskFacade(
            task,
            async resolved =>
            {
                await using var seedScope = provider.CreateAsyncScope();
                var seedDb = seedScope.ServiceProvider.GetRequiredService<AgwDbContext>();
                seedDb.ProjectConversations.Add(
                    new ProjectConversation
                    {
                        Id = resolved.ProjectConversationId,
                        ProjectId = resolved.ProjectId,
                        ContextId = resolved.ContextId,
                        CreateBy = "user-id",
                    }
                );
                await seedDb.SaveChangesAsync(ct);
            }
        ));
        services.AddScoped<
            Agw.Projects.Contracts.History.IConversationTurnStore,
            Agw.Projects.Infrastructure.ConversationTurnStore
        >();
        services.AddScoped<ITurnAcceptanceWriter, Agw.Infrastructure.Agents.TurnAcceptanceWriter>();
        services.AddSingleton<TurnBroadcastRegistry>();
        services.AddSingleton<IProjectRuntimeFacade>(new FakeProjectRuntimeFacade());
        services.AddSingleton<IAgwFileSystemResolver>(kit);
        services.AddScoped<IAgentRuntimeFactory>(sp =>
        {
            var factory = new DatabaseAgentRuntimeFactory(sp.GetRequiredService<AgwDbContext>(), blocker);
            factories.Add(factory);
            return factory;
        });
        services.AddScoped(_ => new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance));
        services.AddScoped(_ => new AgentflowTurnExecutor(null!, null!, NullLogger<AgentflowTurnExecutor>.Instance));
        services.AddScoped<ExecutionContextFactory>();
        services.AddScoped<InProcessExecutionCoordinatorFactory>();
        services.AddScoped<TurnAcceptanceService>();
        services.AddScoped<ExecutionConnectionContextFactory>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.Projects.Add(
                new Project
                {
                    Id = task.ProjectId,
                    Name = ProjectDefaults.DefaultBuiltInName,
                    Type = ProjectType.DefaultBuiltIn,
                    CreateBy = "user-id",
                }
            );
            db.Agents.Add(
                new Agent
                {
                    Id = command.AgentId!.Value,
                    Name = "test",
                    CreateBy = "user-id",
                }
            );
            await db.SaveChangesAsync(ct);
        }
        var scope = provider.CreateAsyncScope();
        var context = scope
            .ServiceProvider.GetRequiredService<ExecutionConnectionContextFactory>()
            .Create("user-id", new NullSink(), CancellationToken.None);
        await using var connection = new ExecutionConnection(
            "connection",
            "user-id",
            scope,
            new ExecutionCommandDispatcher([]),
            context,
            NullLogger.Instance
        );
        await context.ApplySettingsAsync(ExecutionSettings.CreateDefault(), ct);
        await context.StartTurnAsync(command, ct);
        await blocker.Started.Task.WaitAsync(TurnTimeout, ct);
        var runtimeFactory = Assert.Single(factories);
        try
        {
            // Act: a connection command must finish while the runtime still owns an active database operation.
            if (changePermission)
            {
                await context.SetPermissionModeAsync(AgwPermissionMode.AlwaysAsk, ct);
                Assert.Equal(AgwPermissionMode.AlwaysAsk, context.Settings!.PermissionMode);
            }
            else
            {
                var checkpoints = await connection.GetAgentflowCheckpointsAsync(Guid.CreateVersion7(), ct);
                Assert.Empty(checkpoints);
            }
            Assert.True(context.HasActiveTurn);
            Assert.False(runtimeFactory.Disposed);
        }
        finally
        {
            blocker.Release.TrySetResult();
            await context.WhenIdleAsync().WaitAsync(TurnTimeout, ct);
        }

        // Assert: the runtime scope remains valid through completion and is released with the connection.
        Assert.Equal(1, runtimeFactory.AgentCount);
        await connection.DisposeAsync();
        Assert.True(Assert.Single(runtimeFactory.Created).IsDisposed);
        Assert.True(runtimeFactory.Disposed);
    }

    private sealed class BlockingQueryInterceptor : DbCommandInterceptor
    {
        public DbContext? RuntimeContext { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (ReferenceEquals(eventData.Context, RuntimeContext))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>
    /// 在运行作用域的 DbContext 上构造 Runtime；模型调用读取数据库，查询在拦截器中暂停。
    /// Builds Runtimes on the runtime scope's DbContext; the model call reads the database and the query pauses in the interceptor.
    /// </summary>
    private sealed class DatabaseAgentRuntimeFactory : IAgentRuntimeFactory, IAsyncDisposable
    {
        private readonly AgwDbContext _db;
        private readonly BlockingQueryInterceptor _blocker;

        public DatabaseAgentRuntimeFactory(AgwDbContext db, BlockingQueryInterceptor blocker)
        {
            _db = db;
            _blocker = blocker;
        }

        public List<AgentRuntime> Created { get; } = [];
        public bool Disposed { get; private set; }
        public int AgentCount { get; set; }

        public async Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            AgentExecutionTask task,
            ExecutionSettings settings,
            CancellationToken cancellationToken = default
        )
        {
            _blocker.RuntimeContext = _db;
            var agent = new CountingAgent(this, _db);
            var runtime = new AgentRuntime(
                NullLogger.Instance,
                agent,
                await agent.CreateSessionAsync(cancellationToken),
                task.ProjectId,
                task.ContextId,
                sessionStateScope: null
            );
            Created.Add(runtime);
            return runtime;
        }

        public Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(!runtime.IsDisposed);

        public Task<AgentflowNodeAgent?> CreateAgentflowNodeAgentAsync(
            Guid agentId,
            Guid? projectId,
            Guid conversationId,
            IReadOnlyDictionary<string, string>? environmentVariables,
            bool deferHumanInteractions,
            CancellationToken cancellationToken = default,
            AgwPermissionMode? permissionMode = null
        ) => throw new NotSupportedException();

        public Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingAgent : AIAgent
    {
        private readonly DatabaseAgentRuntimeFactory _owner;
        private readonly AgwDbContext _db;

        public CountingAgent(DatabaseAgentRuntimeFactory owner, AgwDbContext db)
        {
            _owner = owner;
            _db = db;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new CountingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new CountingSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            _owner.AgentCount = await _db.Agents.CountAsync(cancellationToken);
            yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
        }

        private sealed class CountingSession : AgentSession;
    }

    [Fact]
    public async Task DetachAsync_IdleRuntime_DisposesAndRemovesImmediately()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Context.WhenIdleAsync();
        var removed = false;

        await connection.DetachAsync(() => removed = true);

        Assert.True(Assert.Single(fixture.Runtimes.Created).IsDisposed);
        Assert.True(removed);
    }

    [Fact]
    public async Task DetachAsync_RunningTurn_RemovesAfterTurnCompletes()
    {
        await using var fixture = await CreateFixtureAsync(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());
        Assert.False(removed.Task.IsCompleted);

        fixture.Runtimes.ReleaseHeldTurns();
        await removed.Task.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(fixture.Runtimes.Created).IsDisposed);
        Assert.False(Assert.Single(fixture.Runtimes.Agents).Canceled);
    }

    [Fact]
    public async Task DetachAsync_WaitingForHuman_InterruptsTurn()
    {
        await using var fixture = await CreateFixtureAsync(requestsApproval: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Sink.InteractionRequested.Task.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());

        await removed.Task.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var finished = Assert.Single(fixture.Sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal("interrupted", finished.AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RecoverInProcessExecution_RunningAfterDetach_StaysBusyUntilCompletion()
    {
        await using var fixture = await CreateFixtureAsync(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var agent = Assert.Single(fixture.Runtimes.Agents);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.DetachAsync(() => removed.TrySetResult());

        Assert.True(await connection.RecoverInProcessExecutionAsync(false, TestContext.Current.CancellationToken));
        Assert.False(agent.Canceled);
        var activeAfterInterrupt = await connection.RecoverInProcessExecutionAsync(
            true,
            TestContext.Current.CancellationToken
        );
        if (!activeAfterInterrupt)
        {
            Assert.True(fixture.Context.WhenIdleAsync().IsCompleted);
        }
        await agent.Cancellation.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await removed.Task.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        Assert.False(await connection.RecoverInProcessExecutionAsync(false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoverInProcessExecution_ForeignOrMissingConnection_DoesNotExposeOrInterruptTurn()
    {
        await using var fixture = await CreateFixtureAsync(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var agent = Assert.Single(fixture.Runtimes.Agents);
        await using var registry = new ExecutionConnectionRegistry(
            null!,
            null!,
            new TestLifetime(),
            NullLoggerFactory.Instance
        );
        var connections =
            (ConcurrentDictionary<string, ExecutionConnection>)
                typeof(ExecutionConnectionRegistry)
                    .GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(registry)!;
        connections["old-connection"] = connection;

        Assert.False(
            await registry.RecoverInProcessExecutionAsync(
                "old-connection",
                "another-user",
                true,
                TestContext.Current.CancellationToken
            )
        );
        Assert.False(
            await registry.RecoverInProcessExecutionAsync(
                "missing",
                "user-id",
                true,
                TestContext.Current.CancellationToken
            )
        );
        Assert.False(agent.Canceled);
        Assert.True(
            await registry.RecoverInProcessExecutionAsync(
                "old-connection",
                "user-id",
                true,
                TestContext.Current.CancellationToken
            )
        );
        await agent.Cancellation.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FindInProcessExecution_OwnActiveConversation_ReturnsOriginalConnectionOnly()
    {
        await using var fixture = await CreateFixtureAsync(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), TestContext.Current.CancellationToken);
        await fixture.Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        await using var registry = new ExecutionConnectionRegistry(
            null!,
            null!,
            new TestLifetime(),
            NullLoggerFactory.Instance
        );
        var connections =
            (ConcurrentDictionary<string, ExecutionConnection>)
                typeof(ExecutionConnectionRegistry)
                    .GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(registry)!;
        connections["before-reload"] = connection;
        var projectId = fixture.Context.ProjectId!.Value;
        var conversationId = fixture.Context.ConversationId!.Value;
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.DetachAsync(() => removed.TrySetResult());
        try
        {
            Assert.Equal(
                "before-reload",
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    conversationId,
                    "user-id",
                    "after-reload",
                    TestContext.Current.CancellationToken
                )
            );
            // 仍在运行的 Turn 对发起它的连接同样可见。A turn still running is also visible to the connection that started it.
            Assert.Equal(
                "before-reload",
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    conversationId,
                    "user-id",
                    "before-reload",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    conversationId,
                    "another-user",
                    "after-reload",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    Guid.CreateVersion7(),
                    conversationId,
                    "user-id",
                    "after-reload",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    Guid.CreateVersion7(),
                    "user-id",
                    "after-reload",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.False(Assert.Single(fixture.Runtimes.Agents).Canceled);
        }
        finally
        {
            fixture.Runtimes.ReleaseHeldTurns();
            await removed.Task.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        }
        Assert.Null(
            await registry.FindInProcessExecutionAsync(
                projectId,
                conversationId,
                "user-id",
                "after-reload",
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task FindInProcessExecution_TurnAfterFinishMessage_IsHiddenOnlyFromItsOwnConnection()
    {
        // Arrange: the connection's turn has written its finish message and is still releasing resources.
        // 准备：连接上的 Turn 已写出结束消息，仍在释放资源。
        var token = TestContext.Current.CancellationToken;
        var sink = new FinishGateSink();
        await using var fixture = await CreateFixtureAsync(messageSink: sink);
        await using var connection = fixture.Connection;
        await using var registry = new ExecutionConnectionRegistry(
            null!,
            null!,
            new TestLifetime(),
            NullLoggerFactory.Instance
        );
        var connections =
            (ConcurrentDictionary<string, ExecutionConnection>)
                typeof(ExecutionConnectionRegistry)
                    .GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(registry)!;
        connections["settling"] = connection;
        string? forItself;
        string? forReloadedPage;
        try
        {
            await fixture.Context.StartTurnAsync(fixture.CreateExecCommand(), token);
            await sink.FirstFinish.WaitAsync(TurnTimeout, token);
            var projectId = fixture.Context.ProjectId!.Value;
            var conversationId = fixture.Context.ConversationId!.Value;

            // Act
            forItself = await registry.FindInProcessExecutionAsync(
                projectId,
                conversationId,
                "user-id",
                "settling",
                token
            );
            forReloadedPage = await registry.FindInProcessExecutionAsync(
                projectId,
                conversationId,
                "user-id",
                "after-reload",
                token
            );
        }
        finally
        {
            sink.ReleaseFinish();
        }
        await fixture.Context.WhenIdleAsync();

        // Assert: its own client already has the result; another page still waits for the release.
        // 断言：它自己的客户端已有结果；另一个页面仍要等待释放完成。
        Assert.Null(forItself);
        Assert.Equal("settling", forReloadedPage);
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }

    private static async Task<ConnectionFixture> CreateFixtureAsync(
        bool holdTurnOpen = false,
        bool requestsApproval = false,
        IExecutionMessageSink? messageSink = null
    )
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var persistence = await TurnPersistenceTestKit.CreateAsync();
        var kit = new InProcessCoordinatorTestKit();
        kit.Runtimes.HoldTurnOpen = holdTurnOpen;
        kit.Runtimes.RequestsApproval = requestsApproval;
        var agents = new TestExecutionAgents();
        var sink = new RecordingSink();
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        var projectTasks = new FakeProjectTaskFacade(task, resolved => persistence.SeedConversationAsync(resolved));
        var context = new ExecutionConnectionContext(
            "user-id",
            messageSink ?? sink,
            CancellationToken.None,
            persistence.CreateAcceptance(projectTasks, new FakeProjectRuntimeFacade()),
            projectTasks,
            kit.CreateFactory(agents.ContextFactory, persistence),
            durableCoordinator: null
        );
        var connection = new ExecutionConnection(
            "connection",
            "user-id",
            provider.CreateAsyncScope(),
            new ExecutionCommandDispatcher([]),
            context,
            NullLogger.Instance
        );
        Assert.Equal("user-id", connection.UserId);
        return new ConnectionFixture(connection, context, kit.Runtimes, agents, sink, persistence);
    }

    private static ExecCommand CreateExecCommand() =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [] })
        {
            AgentId = Guid.CreateVersion7(),
            ConversationId = Guid.CreateVersion7(),
        };

    /// <summary>
    /// 连接测试环境；释放时删除它的测试数据库，连接本身由测试先行释放。
    /// The connection test environment; disposing it deletes its test database, after the test disposes the connection.
    /// </summary>
    private sealed record ConnectionFixture(
        ExecutionConnection Connection,
        ExecutionConnectionContext Context,
        TestAgentRuntimeFactory Runtimes,
        TestExecutionAgents Agents,
        RecordingSink Sink,
        TurnPersistenceTestKit Persistence
    ) : IAsyncDisposable
    {
        public ExecCommand CreateExecCommand()
        {
            var command = ExecutionConnectionTests.CreateExecCommand();
            Agents.Add(command.AgentId!.Value);
            return command;
        }

        public ValueTask DisposeAsync() => Persistence.DisposeAsync();
    }

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(0);

        private readonly ProjectTaskSnapshot _task;
        private readonly Func<AgentExecutionTask, Task> _seed;
        private readonly HashSet<Guid> _savedConversationIds = [];

        /// <summary>
        /// 只有受理时解析过的对话才算已保存，与真实 Facade 一样按项目匹配。
        /// Only a conversation resolved at acceptance counts as saved, matched by project as the real Facade does.
        /// </summary>
        public Task<string?> FindContextIdAsync(
            Guid projectId,
            Guid conversationId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                projectId == _task.ProjectId && _savedConversationIds.Contains(conversationId) ? _task.ContextId : null
            );

        /// <summary>
        /// seed 写入解析出的任务所属的对话，受理事务按真实规则校验它。
        /// seed writes the conversation of the resolved task so the acceptance transaction checks it by the real rules.
        /// </summary>
        public FakeProjectTaskFacade(AgentExecutionTask task, Func<AgentExecutionTask, Task> seed)
        {
            _task = ToSnapshot(task);
            _seed = seed;
        }

        public async Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _savedConversationIds.Add(request.ConversationId);
            var resolved = _task with { ProjectConversationId = request.ConversationId };
            await _seed(
                new AgentExecutionTask
                {
                    TaskId = resolved.TaskId,
                    ProjectId = resolved.ProjectId,
                    ProjectConversationId = resolved.ProjectConversationId,
                    ContextId = resolved.ContextId,
                }
            );
            return resolved;
        }

        public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectTaskSnapshot> GetOrCreateAsync(
            StartProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> FinishAsync(
            FinishProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, Guid>> ResolveConversationIdsAsync(
            IReadOnlyCollection<Guid> taskIds,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FakeProjectRuntimeFacade : IProjectRuntimeFacade
    {
        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ProjectRuntimeSnapshot?>(
                new ProjectRuntimeSnapshot(
                    projectId,
                    "project",
                    AppContext.BaseDirectory,
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(AppContext.BaseDirectory);
    }

    private static ProjectTaskSnapshot ToSnapshot(AgentExecutionTask task) =>
        new(
            task.TaskId,
            task.ProjectConversationId,
            task.ProjectId,
            task.ContextId,
            task.JobId,
            task.Title,
            ProjectTaskStatus.Pending,
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.FinishedTime
        );

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IExecutionMessageSink
    {
        private readonly ConcurrentQueue<AgwMessage> _messages = new();

        public TaskCompletionSource InteractionRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgwMessage> Messages => _messages.ToArray();

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            _messages.Enqueue(message);
            if (AgwMessageClassifier.IsInteractionRequest(message))
                InteractionRequested.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
