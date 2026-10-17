using Agw.Auth.Contracts;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Jobs;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Jobs.Tests;

[Trait("Category", "Postgres")]
public sealed class JobLockCancellationPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PostgresLeaseLost_CancelsDatabaseOperation(bool durableRecovery)
    {
        var token = TestContext.Current.CancellationToken;
        var connectionString = PostgresJobTestDatabase.RequireAdminConnectionString();
        var gate = new DatabaseCommandGate(command =>
            command.CommandText.Contains("FROM job", StringComparison.Ordinal)
        );
        await using var database = await PostgresJobTestDatabase.CreateAsync(connectionString, token, gate);
        var job = await SeedJobAsync(database, durableRecovery, token);
        var applicationName = $"agw-job-cancellation-{Guid.CreateVersion7():N}";
        using var settings = CreateSettings(database.ConnectionString, applicationName);
        var projectLock = CreateRouter(settings);
        await using var provider = JobTestServices.Create(database.Options, TimeProvider.System, projectLock);
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        using var recovery = ActivatorUtilities.CreateInstance<DurableJobRecoveryHostedService>(provider);
        Assert.False(UserInfoUtil.IsContextActive);
        var operation = durableRecovery
            ? DurableJobRecoveryHostedServiceTests.RecoverJobAsync(recovery, job, token)
            : JobHostedServiceTests.ExecuteOneAsync(scheduler, ScheduledJob.FromJob(job), token);
        try
        {
            var operationToken = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await TerminateLockConnectionAsync(database.ConnectionString, applicationName, token);
            await gate.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(operationToken.IsCancellationRequested);
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(10), token));
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        finally
        {
            gate.Release();
            await Record.ExceptionAsync(() => operation);
        }

        using var verificationScope = UserInfoUtil.PushSystemScope();
        await using var verification = database.CreateContext();
        var persisted = await verification.Jobs.SingleAsync(token);
        Assert.Equal(job.Status, persisted.Status);
        Assert.Equal(job.ActiveTurnId, persisted.ActiveTurnId);
        Assert.Empty(await verification.JobLogs.ToListAsync(token));
    }

    [Fact]
    public async Task TryAcquireAsync_PostgresContention_ReturnsNullUntilLeaseReleased()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgresJobTestDatabase.CreateAsync(
            PostgresJobTestDatabase.RequireAdminConnectionString(),
            token
        );
        using var settings = CreateSettings(database.ConnectionString, $"agw-job-contention-{Guid.CreateVersion7():N}");
        var first = CreateRouter(settings);
        var second = CreateRouter(settings);
        var projectId = Guid.CreateVersion7();
        await using (var held = await first.AcquireAsync(projectId, token))
        {
            Assert.True(held.HandleLostToken.CanBeCanceled);
            Assert.Null(await second.TryAcquireAsync(projectId, token));
        }

        await using var acquired = await second.TryAcquireAsync(projectId, token);
        Assert.NotNull(acquired);
        Assert.True(acquired.HandleLostToken.CanBeCanceled);
    }

    [Fact]
    public async Task RecoverAsync_PostgresLeaseLost_CancelsInProcessRecovery()
    {
        var token = TestContext.Current.CancellationToken;
        var gate = new DatabaseCommandGate(command =>
            command.CommandText.Contains("FROM job", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
        );
        await using var database = await PostgresJobTestDatabase.CreateAsync(
            PostgresJobTestDatabase.RequireAdminConnectionString(),
            token,
            gate
        );
        var job = await SeedJobAsync(database, running: true, token);
        var applicationName = $"agw-in-process-recovery-{Guid.CreateVersion7():N}";
        using var settings = CreateSettings(database.ConnectionString, applicationName);
        var projectLock = CreateRouter(settings);
        await using var provider = JobTestServices.Create(database.Options, TimeProvider.System, projectLock);
        var recovery = ActivatorUtilities.CreateInstance<InProcessJobRecovery>(provider);
        Assert.False(UserInfoUtil.IsContextActive);
        var operation = recovery.RecoverAsync(token);
        try
        {
            var operationToken = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await TerminateLockConnectionAsync(database.ConnectionString, applicationName, token);
            await gate.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(operationToken.IsCancellationRequested);
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(10), token));
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        finally
        {
            gate.Release();
            await Record.ExceptionAsync(() => operation);
        }

        using var verificationScope = UserInfoUtil.PushSystemScope();
        await using var verification = database.CreateContext();
        var persisted = await verification.Jobs.SingleAsync(token);
        Assert.Equal(JobStatus.Running, persisted.Status);
        Assert.Equal(job.ActiveTurnId, persisted.ActiveTurnId);
        Assert.Empty(await verification.JobLogs.ToListAsync(token));
    }

    private static ServiceProvider CreateSettings(string connectionString, string applicationName) =>
        new ServiceCollection()
            .Configure<DatabaseSettings>(settings =>
            {
                settings.Provider = DatabaseProvider.Postgres;
                settings.ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                    Pooling = false,
                }.ConnectionString;
            })
            .AddOptions<DistributedLockSettings>()
            .Services.BuildServiceProvider();

    private static ProjectExecutionLockRouter CreateRouter(IServiceProvider settings) =>
        new(
            settings.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            settings.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            new InMemoryProjectExecutionLock(),
            (_, connectionString) =>
                new PostgresDistributedSynchronizationProvider(
                    connectionString,
                    options => options.KeepaliveCadence(TimeSpan.FromSeconds(1))
                )
        );

    private static async Task<Job> SeedJobAsync(PostgresJobTestDatabase database, bool running, CancellationToken token)
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "lease-loss",
            Status = running ? JobStatus.Running : JobStatus.Pending,
            IsEnabled = true,
            NextRunTime = TimeProvider.System.GetUtcNow(),
            CreateBy = "owner",
            ActiveTurnId = running ? Guid.CreateVersion7() : null,
            ActiveAttemptStartedAt = running ? TimeProvider.System.GetUtcNow() : null,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
        };
        using var seedScope = UserInfoUtil.PushSystemScope();
        await using var seed = database.CreateContext();
        seed.Jobs.Add(job);
        await seed.SaveChangesAsync(token);
        return job;
    }

    private static async Task TerminateLockConnectionAsync(
        string connectionString,
        string applicationName,
        CancellationToken token
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @name AND pid <> pg_backend_pid()",
            connection
        );
        command.Parameters.AddWithValue("name", applicationName);
        Assert.Equal(true, await command.ExecuteScalarAsync(token));
    }
}
