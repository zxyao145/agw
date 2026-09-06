using System.Security.Claims;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Coordination;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Agents.Tests;

public sealed class PostgresExecutionFencingTests
{
    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Set AGW_TEST_POSTGRES_CONNECTION_STRING to an isolated PostgreSQL instance with CREATE DATABASE permission."
    )]
    public async Task LeaseConnectionLost_NewWorkerClaims_OldResultCannotCommit()
    {
        var token = TestContext.Current.CancellationToken;
        using var user = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
        var databaseName = "agw_fencing_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync(token);
        }
        try
        {
            settings.Database = databaseName;
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseNpgsql(settings.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var firstContext = new AgwDbContext(options);
            await firstContext.Database.EnsureCreatedAsync(token);
            var task = new AgentExecutionTask
            {
                TaskId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                ProjectConversationId = Guid.NewGuid(),
                ContextId = "postgres-fencing",
            };
            firstContext.Projects.Add(new Project { Id = task.ProjectId, CreateBy = "owner" });
            firstContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = task.ProjectConversationId,
                    ProjectId = task.ProjectId,
                    ContextId = task.ContextId,
                    CreateBy = "owner",
                }
            );
            await firstContext.SaveChangesAsync(token);
            var firstStore = new DurableExecutionStore(
                firstContext,
                TimeProvider.System,
                InMemoryApplicationLock.Shared,
                TestDurablePersistence.Create(firstContext)
            );
            var executionId = Guid.NewGuid();
            await firstStore.RegisterAsync(
                executionId,
                "owner",
                Guid.NewGuid(),
                AgentRuntimeType.Agent,
                new AgwUserInput { Contents = [] },
                task,
                ExecutionSettings.FromCommand(new SettingCommand(task.ProjectId, contextId: task.ContextId)),
                token
            );

            settings.ApplicationName = databaseName;
            var services = new ServiceCollection();
            services.Configure<DistributedLockSettings>(value =>
            {
                value.Provider = DistributedLockProvider.Postgres;
                value.ConnectionString = settings.ConnectionString;
            });
            await using var provider = services.BuildServiceProvider();
            var router = new ApplicationLockRouter(
                new InitializationState(settings.ConnectionString),
                provider.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
                new InMemoryApplicationLock(),
                (_, connectionString) =>
                    new PostgresDistributedSynchronizationProvider(
                        connectionString,
                        options =>
                        {
                            options.UseMultiplexing(false);
                            options.KeepaliveCadence(TimeSpan.FromMilliseconds(100));
                        }
                    )
            );
            await using var firstLease = await router.AcquireAsync(
                DurableExecutionLock.GetResourceName(executionId),
                token
            );
            var first = await firstStore.TryBeginSegmentAsync(executionId, DateTimeOffset.MaxValue, token);
            Assert.NotNull(first);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = firstLease.HandleLostToken.Register(() => lost.TrySetResult());
            using var segment = CancellationTokenSource.CreateLinkedTokenSource(token, firstLease.HandleLostToken);
            await using (
                var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND application_name = @application",
                    admin
                )
            )
            {
                terminate.Parameters.AddWithValue("database", databaseName);
                terminate.Parameters.AddWithValue("application", databaseName);
                Assert.Equal(true, await terminate.ExecuteScalarAsync(token));
            }
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.True(segment.IsCancellationRequested);

            await using var secondLease = await router.AcquireAsync(
                DurableExecutionLock.GetResourceName(executionId),
                token
            );
            await using var secondContext = new AgwDbContext(options);
            var secondStore = new DurableExecutionStore(
                secondContext,
                TimeProvider.System,
                InMemoryApplicationLock.Shared,
                TestDurablePersistence.Create(secondContext)
            );
            var second = await secondStore.TryBeginSegmentAsync(executionId, DateTimeOffset.MaxValue, token);
            Assert.NotNull(second);
            var result = new DurableExecutionSegmentResult
            {
                ExecutionId = executionId,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Completed,
            };
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                firstStore.SaveSegmentResultAsync(result, first.StateVersion, token)
            );
            Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, error.Code);
            var completed = await secondStore.SaveSegmentResultAsync(result, second.StateVersion, token);
            Assert.Equal(Agw.Shared.Data.Entities.Executions.DurableExecutionStatus.Completed, completed.Status);

            var agentId = Guid.NewGuid();
            secondContext.Agents.Add(
                new Agw.Shared.Data.Entities.Agents.Agent
                {
                    Id = agentId,
                    CreateBy = "owner",
                    Name = "reset-agent",
                }
            );
            await secondContext.SaveChangesAsync(token);
            var sessions = new Agw.Infrastructure.Agents.AgentSessionStatePersistence(secondContext, router);
            Assert.True(
                await sessions.SaveAsync(
                    task.ProjectId,
                    task.ProjectConversationId,
                    agentId,
                    "",
                    "old-session",
                    "owner",
                    TimeProvider.System.GetUtcNow(),
                    token
                )
            );
            await using var clearContext = new AgwDbContext(options);
            var clear = new Agw.Infrastructure.Projects.ProjectDeletionCoordinator(
                clearContext,
                router,
                new Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance(
                    clearContext,
                    router,
                    TimeProvider.System,
                    Microsoft
                        .Extensions
                        .Logging
                        .Abstractions
                        .NullLogger<Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance>
                        .Instance
                )
            );
            var target = new Agw.Projects.Application.Persistence.ProjectConversationDeletionTarget(
                task.ProjectId,
                task.ProjectConversationId,
                task.ContextId,
                "owner"
            );
            Assert.True(await clear.ClearConversationRecordsAsync(target, token));
            Assert.Empty(await clearContext.AgentSessionStates.ToListAsync(token));
            Assert.Equal(1, (await clearContext.ProjectConversations.AsNoTracking().SingleAsync(token)).Generation);
            var staleSave = await Assert.ThrowsAsync<AgwException>(() =>
                sessions.SaveAsync(
                    task.ProjectId,
                    task.ProjectConversationId,
                    agentId,
                    "",
                    "late-old-session",
                    "owner",
                    TimeProvider.System.GetUtcNow(),
                    token
                )
            );
            Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, staleSave.Code);
            secondContext.ChangeTracker.Clear();
            Assert.True(
                await sessions.SaveAsync(
                    task.ProjectId,
                    task.ProjectConversationId,
                    agentId,
                    "",
                    "new-session",
                    "owner",
                    TimeProvider.System.GetUtcNow(),
                    token,
                    1
                )
            );
            var gate = new Agw.Infrastructure.Projects.ConversationExecutionGate(
                secondContext,
                router,
                TimeProvider.System
            );
            await using var newTurn = await gate.AcquireAsync(task.ProjectConversationId, 1, token);
            await Assert.ThrowsAsync<AgwException>(() => clear.ClearConversationRecordsAsync(target, token));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private sealed class InitializationState : IServerInitializationState
    {
        public InitializationState(string connectionString)
        {
            DatabaseConnectionString = connectionString;
        }

        public bool IsInitialized => true;
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Postgres;
        public string DatabaseConnectionString { get; }
    }
}
