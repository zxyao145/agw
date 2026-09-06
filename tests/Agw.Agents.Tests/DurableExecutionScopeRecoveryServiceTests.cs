using Agw.Agents.Application.Persistence;
using Agw.Infrastructure;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionScopeRecoveryServiceTests
{
    [Theory]
    [InlineData("InProcess")]
    [InlineData("Distributed")]
    public void AddInfrastructure_AnyExecutionMode_RegistersRecoveryService(string executionMode)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "sqlite",
                    ["Execution:Provider"] = executionMode,
                }
            )
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddInfrastructure(configuration);

        // Assert
        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DurableExecutionScopeRecoveryService)
        );
    }

    [Fact]
    public async Task ExecuteAsync_BacklogBeyondOnePass_CompletesWithoutExecutionWorkerOrUserRetry()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var seed = new AgwDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(token);
            seed.AddRange(
                Enumerable
                    .Range(0, 600)
                    .Select(index =>
                        DurableExecutionScopeMaintenanceTests.CreateExecution(
                            Guid.CreateVersion7(),
                            Guid.CreateVersion7(),
                            index < 300 ? "first-owner" : "second-owner"
                        )
                    )
            );
            await seed.SaveChangesAsync(token);
        }
        var scopes = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref scopes);
            return new AgwDbContext(options);
        });
        services.AddScoped<IDurableExecutionScopeMaintenance>(provider =>
            TestDurablePersistence.Create(provider.GetRequiredService<AgwDbContext>())
        );
        await using var provider = services.BuildServiceProvider();
        using var recovery = new DurableExecutionScopeRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new InitializationState(true),
            TimeProvider.System,
            NullLogger<DurableExecutionScopeRecoveryService>.Instance
        );

        // Act
        try
        {
            await recovery.StartAsync(token);
            await recovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
        finally
        {
            await recovery.StopAsync(token);
        }

        // Assert
        Assert.True(scopes >= 2);
        using var system = UserInfoUtil.PushSystemScope();
        await using var context = new AgwDbContext(options);
        Assert.Equal(
            600,
            await context.DurableExecutions.CountAsync(
                row => row.ScopeBackfilled && row.ProjectId != null && row.ProjectConversationId != null,
                token
            )
        );
        Assert.False(await context.DurableExecutions.AnyAsync(row => !row.ScopeBackfilled, token));
    }

    [Fact]
    public async Task ExecuteAsync_Uninitialized_WaitsUntilSetupCompletes()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var state = new InitializationState(false);
        var calls = 0;
        var services = new ServiceCollection();
        services.AddScoped<IDurableExecutionScopeMaintenance>(_ => new StubMaintenance(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new DurableExecutionScopeBackfillResult(null, false));
            }
        ));
        await using var provider = services.BuildServiceProvider();
        using var recovery = new DurableExecutionScopeRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            TimeProvider.System,
            NullLogger<DurableExecutionScopeRecoveryService>.Instance
        );

        int callsBeforeSetup;

        // Act
        try
        {
            await recovery.StartAsync(token);
            await state.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            callsBeforeSetup = Volatile.Read(ref calls);
            state.IsInitialized = true;
            await recovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally
        {
            await recovery.StopAsync(token);
        }

        // Assert
        Assert.Equal(0, callsBeforeSetup);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedCancellation_LogsAndRetriesWithSameCursorInNewScope()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var cursor = new DurableExecutionScopeCursor("owner", Guid.CreateVersion7());
        var received = new List<DurableExecutionScopeCursor?>();
        var scopes = 0;
        var services = new ServiceCollection();
        services.AddScoped<IDurableExecutionScopeMaintenance>(_ =>
        {
            Interlocked.Increment(ref scopes);
            return new StubMaintenance(
                (after, _) =>
                {
                    received.Add(after);
                    if (received.Count == 2)
                        return Task.FromException<DurableExecutionScopeBackfillResult>(
                            new OperationCanceledException("Unexpected provider cancellation.")
                        );
                    return Task.FromResult(
                        new DurableExecutionScopeBackfillResult(
                            received.Count == 1 ? cursor : null,
                            received.Count == 1
                        )
                    );
                }
            );
        });
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        using var recovery = new DurableExecutionScopeRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new InitializationState(true),
            TimeProvider.System,
            logger
        );

        // Act
        try
        {
            await recovery.StartAsync(token);
            await recovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            await recovery.StopAsync(token);
        }

        // Assert
        Assert.Equal(new DurableExecutionScopeCursor?[] { null, cursor, cursor }, received);
        Assert.Equal(3, scopes);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
    }

    [Fact]
    public async Task StopAsync_WaitingForSetup_ExitsWithoutError()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var state = new InitializationState(false);
        var logger = new RecordingLogger();
        using var recovery = new DurableExecutionScopeRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            TimeProvider.System,
            logger
        );
        await recovery.StartAsync(token);
        await state.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // Act
        await recovery.StopAsync(token);

        // Assert
        Assert.True(recovery.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(logger.Levels);
    }

    [Fact]
    public async Task StopAsync_BackfillInFlight_CancelsWithoutError()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped<IDurableExecutionScopeMaintenance>(_ => new StubMaintenance(
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new DurableExecutionScopeBackfillResult(null, false);
            }
        ));
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        using var recovery = new DurableExecutionScopeRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new InitializationState(true),
            TimeProvider.System,
            logger
        );
        await recovery.StartAsync(token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // Act
        await recovery.StopAsync(token);

        // Assert
        Assert.True(recovery.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(logger.Levels);
    }

    private sealed class InitializationState : IServerInitializationState
    {
        private volatile bool _initialized;

        public InitializationState(bool initialized)
        {
            _initialized = initialized;
        }

        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInitialized
        {
            get
            {
                var initialized = _initialized;
                Checked.TrySetResult();
                return initialized;
            }
            set => _initialized = value;
        }
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Sqlite;
        public string DatabaseConnectionString => "Data Source=:memory:";
    }

    private sealed class StubMaintenance : IDurableExecutionScopeMaintenance
    {
        public Task<bool> IsSessionCurrentAsync(
            Guid projectId,
            Guid conversationId,
            string ownerUserId,
            int expectedGeneration,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);

        private readonly Func<
            DurableExecutionScopeCursor?,
            CancellationToken,
            Task<DurableExecutionScopeBackfillResult>
        > _backfill;

        public StubMaintenance(
            Func<DurableExecutionScopeCursor?, CancellationToken, Task<DurableExecutionScopeBackfillResult>> backfill
        )
        {
            _backfill = backfill;
        }

        public Task<DurableExecutionScopeBackfillResult> BackfillAsync(
            CancellationToken cancellationToken = default,
            DurableExecutionScopeCursor? after = null
        ) => _backfill(after, cancellationToken);

        public Task<bool> RepairAndCheckActiveExecutionsAsync(
            Guid projectId,
            Guid conversationId,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<bool> ValidateLockedExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DurableExecutionRecord?> LoadValidatedExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<DurableExecutionScopeRecoveryService>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Levels.Add(logLevel);
    }
}
