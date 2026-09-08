using System.Reflection;
using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Jobs.Tests;

public sealed class DurableJobRecoveryHostedServiceTests
{
    [Fact]
    public async Task RecoverJobAsync_ExecutionWasNotRegistered_RecordsCurrentAttemptFailure()
    {
        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Status = JobStatus.Running,
            IsEnabled = true,
            ActiveExecutionId = executionId,
            ActiveAttemptStartedAt = TimeProvider.System.GetUtcNow(),
            CreateBy = "owner",
        };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var dbContext = new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
        );
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var recorder = new RecordingOutcomeRecorder();
        var services = new ServiceCollection()
            .AddSingleton<IJobsDbContext>(dbContext)
            .AddSingleton<IDurableAgentExecutionFacade>(new MissingDurableExecutionFacade())
            .AddSingleton<IJobAttemptOutcomeRecorder>(recorder)
            .BuildServiceProvider();
        var timeProvider = TimeProvider.System;
        var service = new DurableJobRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new InitializationState(true),
            new ImmediateProjectExecutionLock(),
            timeProvider,
            new JobSchedulerWakeSignal(timeProvider),
            NullLogger<DurableJobRecoveryHostedService>.Instance
        );
        var method = typeof(DurableJobRecoveryHostedService).GetMethod(
            "RecoverJobAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        var invocation = method!.Invoke(service, [job, TestContext.Current.CancellationToken]);
        await Assert.IsAssignableFrom<Task>(invocation);

        Assert.Equal(job.Id, recorder.JobId);
        Assert.Equal(executionId, recorder.ExecutionId);
        Assert.False(recorder.Success);
        Assert.Contains("was not registered", recorder.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverJobAsync_MissingOwner_RecordsFailureWithoutRetryingForever()
    {
        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Status = JobStatus.Running,
            IsEnabled = true,
            ActiveExecutionId = executionId,
            ActiveAttemptStartedAt = TimeProvider.System.GetUtcNow(),
            CreateBy = string.Empty,
        };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var dbContext = new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
        );
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var recorder = new RecordingOutcomeRecorder();
        var executionFacade = new MissingDurableExecutionFacade();
        var services = new ServiceCollection()
            .AddSingleton<IJobsDbContext>(dbContext)
            .AddSingleton<IDurableAgentExecutionFacade>(executionFacade)
            .AddSingleton<IJobAttemptOutcomeRecorder>(recorder)
            .BuildServiceProvider();
        var timeProvider = TimeProvider.System;
        var service = new DurableJobRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new InitializationState(true),
            new ImmediateProjectExecutionLock(),
            timeProvider,
            new JobSchedulerWakeSignal(timeProvider),
            NullLogger<DurableJobRecoveryHostedService>.Instance
        );
        var method = typeof(DurableJobRecoveryHostedService).GetMethod(
            "RecoverJobAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        var invocation = method!.Invoke(service, [job, TestContext.Current.CancellationToken]);
        await Assert.IsAssignableFrom<Task>(invocation);

        Assert.Equal(job.Id, recorder.JobId);
        Assert.Equal(executionId, recorder.ExecutionId);
        Assert.False(recorder.Success);
        Assert.Equal("The Job owner is missing.", recorder.ErrorMessage);
        Assert.Equal(0, executionFacade.GetOutcomeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BeforeInitialization_WaitsWithoutQueryingDatabase(bool finishSetup)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Status = JobStatus.Running,
            ActiveExecutionId = Guid.NewGuid(),
            ActiveAttemptStartedAt = TimeProvider.System.GetUtcNow(),
            CreateBy = "owner",
        };
        await using (var seed = new AgwDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(token);
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(token);
        }
        var state = new InitializationState(false);
        var recorder = new RecordingOutcomeRecorder();
        var databaseAccesses = 0;
        await using var provider = new ServiceCollection()
            .AddScoped<IJobsDbContext>(_ =>
            {
                Interlocked.Increment(ref databaseAccesses);
                return new AgwDbContext(options);
            })
            .AddSingleton<IDurableAgentExecutionFacade>(new MissingDurableExecutionFacade())
            .AddSingleton<IJobAttemptOutcomeRecorder>(recorder)
            .BuildServiceProvider();
        using var service = new DurableJobRecoveryHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            new ImmediateProjectExecutionLock(),
            TimeProvider.System,
            new JobSchedulerWakeSignal(TimeProvider.System),
            NullLogger<DurableJobRecoveryHostedService>.Instance
        );
        try
        {
            await service.StartAsync(token);
            await state.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(0, Volatile.Read(ref databaseAccesses));
            if (finishSetup)
            {
                state.IsInitialized = true;
                await recorder.Recorded.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        }
        finally
        {
            await service.StopAsync(token);
        }

        Assert.Equal(finishSetup ? job.Id : (Guid?)null, recorder.JobId);
        Assert.Equal(finishSetup, Volatile.Read(ref databaseAccesses) > 0);
    }

    private sealed class InitializationState : IServerInitializationState
    {
        private volatile bool _isInitialized;

        public InitializationState(bool isInitialized)
        {
            _isInitialized = isInitialized;
        }

        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInitialized
        {
            get
            {
                Checked.TrySetResult();
                return _isInitialized;
            }
            set => _isInitialized = value;
        }
    }

    private sealed class MissingDurableExecutionFacade : IDurableAgentExecutionFacade
    {
        public int GetOutcomeCalls { get; private set; }

        public Task<AgentExecutionResult> GetOutcomeAsync(
            Guid executionId,
            string ownerUserId,
            CancellationToken cancellationToken = default
        )
        {
            GetOutcomeCalls++;
            throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        }

        public async IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
            Guid executionId,
            string ownerUserId,
            string? afterCursor,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> InterruptAsync(
            Guid executionId,
            string ownerUserId,
            string reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingOutcomeRecorder : IJobAttemptOutcomeRecorder
    {
        public TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? JobId { get; private set; }
        public Guid? ExecutionId { get; private set; }
        public bool Success { get; private set; }
        public string? ErrorMessage { get; private set; }

        public Task<JobAttemptResult> RecordAsync(
            Guid jobId,
            Guid executionId,
            bool success,
            string? errorMessage,
            CancellationToken cancellationToken
        )
        {
            JobId = jobId;
            ExecutionId = executionId;
            Success = success;
            ErrorMessage = errorMessage;
            Recorded.TrySetResult();
            return Task.FromResult<JobAttemptResult>(new JobAttemptResult.Drop());
        }
    }

    private sealed class ImmediateProjectExecutionLock : IProjectExecutionLock
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
