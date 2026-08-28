using System.Linq.Expressions;
using System.Reflection;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
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
        var recorder = new RecordingOutcomeRecorder();
        var services = new ServiceCollection()
            .AddSingleton<IRepository<Job>>(new InMemoryJobRepository(job))
            .AddSingleton<IDurableAgentExecutionFacade>(new MissingDurableExecutionFacade())
            .AddSingleton<IJobAttemptOutcomeRecorder>(recorder)
            .BuildServiceProvider();
        var timeProvider = TimeProvider.System;
        var service = new DurableJobRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
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
        var recorder = new RecordingOutcomeRecorder();
        var executionFacade = new MissingDurableExecutionFacade();
        var services = new ServiceCollection()
            .AddSingleton<IRepository<Job>>(new InMemoryJobRepository(job))
            .AddSingleton<IDurableAgentExecutionFacade>(executionFacade)
            .AddSingleton<IJobAttemptOutcomeRecorder>(recorder)
            .BuildServiceProvider();
        var timeProvider = TimeProvider.System;
        var service = new DurableJobRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
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
            return Task.FromResult<JobAttemptResult>(new JobAttemptResult.Drop());
        }
    }

    private sealed class InMemoryJobRepository(Job job) : IRepository<Job>
    {
        public IQueryable<Job> Queryable => new[] { job }.AsQueryable();

        public Task<Job?> GetByIdAsync(object id) => Task.FromResult<Job?>(Equals(job.Id, id) ? job : null);

        public Task<Job?> SingleOrDefaultAsync(
            Expression<Func<Job, bool>> predicate,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new[] { job }.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<Job>> ListAsync(
            Expression<Func<Job, bool>>? predicate = null,
            Func<IQueryable<Job>, IOrderedQueryable<Job>>? orderBy = null
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Job>> ListAsync(
            Expression<Func<Job, bool>>? predicate = null,
            Func<IQueryable<Job>, IOrderedQueryable<Job>>? orderBy = null,
            params Expression<Func<Job, object>>[] includes
        ) => throw new NotSupportedException();

        public Task AddAsync(Job entity) => throw new NotSupportedException();

        public void Update(Job entity) => throw new NotSupportedException();

        public void Remove(Job entity) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
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
