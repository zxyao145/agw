using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Inbound.SignalR;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecutionConnectionTests
{
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
        var factories = new List<DatabaseRuntimeFactory>();
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
        services.AddSingleton<IProjectTaskFacade>(new FakeProjectTaskFacade(task));
        services.AddSingleton<IProjectRuntimeFacade>(new FakeProjectRuntimeFacade());
        services.AddScoped<IRuntimeFactory>(sp =>
        {
            var factory = new DatabaseRuntimeFactory(sp.GetRequiredService<AgwDbContext>(), blocker);
            factories.Add(factory);
            return factory;
        });
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
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
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
            await runtimeFactory.Runtime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        }

        // Assert: the runtime scope remains valid through completion and is released with the connection.
        Assert.Equal(1, runtimeFactory.AgentCount);
        await connection.DisposeAsync();
        Assert.True(runtimeFactory.Runtime.Disposed);
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

    private sealed class DatabaseRuntimeFactory : IRuntimeFactory, IAsyncDisposable
    {
        private readonly AgwDbContext _db;
        private readonly BlockingQueryInterceptor _blocker;
        public TestRuntime Runtime { get; } = new();
        public bool Disposed { get; private set; }
        public int AgentCount { get; private set; }

        public DatabaseRuntimeFactory(AgwDbContext db, BlockingQueryInterceptor blocker)
        {
            _db = db;
            _blocker = blocker;
        }

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
        {
            _blocker.RuntimeContext = _db;
            var turn = Runtime.StartTurn(
                request.TurnContext,
                new RuntimeTurnContextAccessor(),
                new CancellationTokenSource(),
                () => { },
                async ct =>
                {
                    AgentCount = await _db.Agents.CountAsync(ct);
                }
            );
            return Task.FromResult(new RuntimeStartResult(Runtime, turn));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DetachAsync_IdleRuntime_DisposesAndRemovesImmediately()
    {
        var fixture = CreateFixture(holdTurnOpen: false);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        var removed = false;

        await connection.DetachAsync(() => removed = true);

        Assert.True(fixture.RuntimeFactory.Runtime.Disposed);
        Assert.True(removed);
    }

    [Fact]
    public async Task DetachAsync_RunningTurn_RemovesAfterTurnCompletes()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());
        Assert.False(removed.Task.IsCompleted);

        fixture.RuntimeFactory.CompleteTurn();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.RuntimeFactory.Runtime.Disposed);
    }

    [Fact]
    public async Task DetachAsync_WaitingForHuman_InterruptsTurn()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        fixture.RuntimeFactory.StartRequest!.TurnContext.PendingInteractionCountChanged!(1);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());

        Assert.True(fixture.RuntimeFactory.TurnCancellation!.IsCancellationRequested);
        fixture.RuntimeFactory.CompleteTurn();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecoverInProcessExecution_RunningAfterDetach_StaysBusyUntilCompletion()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.DetachAsync(() => removed.TrySetResult());

        Assert.True(await connection.RecoverInProcessExecutionAsync(false, TestContext.Current.CancellationToken));
        Assert.False(fixture.RuntimeFactory.TurnCancellation!.IsCancellationRequested);
        Assert.True(await connection.RecoverInProcessExecutionAsync(true, TestContext.Current.CancellationToken));
        Assert.True(fixture.RuntimeFactory.TurnCancellation.IsCancellationRequested);

        fixture.RuntimeFactory.CompleteTurn();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(await connection.RecoverInProcessExecutionAsync(false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoverInProcessExecution_ForeignOrMissingConnection_DoesNotExposeOrInterruptTurn()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
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
        Assert.False(fixture.RuntimeFactory.TurnCancellation!.IsCancellationRequested);
        Assert.True(
            await registry.RecoverInProcessExecutionAsync(
                "old-connection",
                "user-id",
                true,
                TestContext.Current.CancellationToken
            )
        );
        Assert.True(fixture.RuntimeFactory.TurnCancellation.IsCancellationRequested);
        fixture.RuntimeFactory.CompleteTurn();
    }

    [Fact]
    public async Task FindInProcessExecution_OwnActiveConversation_ReturnsOriginalConnectionOnly()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
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
        var contextId = fixture.Context.ContextId!;
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.DetachAsync(() => removed.TrySetResult());
        try
        {
            Assert.Equal(
                "before-reload",
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    contextId,
                    "user-id",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    contextId,
                    "another-user",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    Guid.CreateVersion7(),
                    contextId,
                    "user-id",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await registry.FindInProcessExecutionAsync(
                    projectId,
                    "another-context",
                    "user-id",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.False(fixture.RuntimeFactory.TurnCancellation!.IsCancellationRequested);
        }
        finally
        {
            fixture.RuntimeFactory.CompleteTurn();
            await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Null(
            await registry.FindInProcessExecutionAsync(
                projectId,
                contextId,
                "user-id",
                TestContext.Current.CancellationToken
            )
        );
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }

    private static ConnectionFixture CreateFixture(bool holdTurnOpen)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var runtimeFactory = new FakeRuntimeFactory(holdTurnOpen);
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        var context = new ExecutionConnectionContext(
            "user-id",
            new NullSink(),
            CancellationToken.None,
            runtimeFactory,
            new FakeProjectTaskFacade(task),
            new FakeProjectRuntimeFacade()
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
        return new ConnectionFixture(connection, context, runtimeFactory);
    }

    private static ExecCommand CreateExecCommand() =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [] })
        {
            AgentId = Guid.CreateVersion7(),
            ConversationId = Guid.CreateVersion7(),
        };

    private sealed record ConnectionFixture(
        ExecutionConnection Connection,
        ExecutionConnectionContext Context,
        FakeRuntimeFactory RuntimeFactory
    );

    private sealed class FakeRuntimeFactory : IRuntimeFactory
    {
        private readonly bool _holdTurnOpen;
        private TaskCompletionSource? _completion;

        public FakeRuntimeFactory(bool holdTurnOpen)
        {
            _holdTurnOpen = holdTurnOpen;
        }

        public TestRuntime Runtime { get; } = new();

        public CancellationTokenSource? TurnCancellation { get; private set; }

        public RuntimeStartRequest? StartRequest { get; private set; }

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
        {
            StartRequest = request;
            if (!_holdTurnOpen)
            {
                return Task.FromResult(
                    new RuntimeStartResult(Runtime, new ActiveTurn(Task.CompletedTask, new CancellationTokenSource()))
                );
            }

            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TurnCancellation = new CancellationTokenSource();
            var turn = new ActiveTurn(_completion.Task, TurnCancellation);
            Runtime.TryStartTurn(turn);
            return Task.FromResult(new RuntimeStartResult(Runtime, turn));
        }

        public void CompleteTurn() => _completion!.TrySetResult();
    }

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(0);

        private readonly ProjectTaskSnapshot _task;

        public FakeProjectTaskFacade(AgentExecutionTask task)
        {
            _task = ToSnapshot(task);
        }

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_task with { ProjectConversationId = request.ConversationId });

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

        public Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
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
                    "/workspace",
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("/workspace");
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

    private sealed class TestRuntime : RuntimeBase
    {
        public bool Disposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Disposed = true;
        }
    }

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
