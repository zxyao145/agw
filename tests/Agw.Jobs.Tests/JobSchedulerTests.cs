using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Enums;
using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Tests;

public class JobSchedulerTests
{
    [Fact]
    public async Task RunAsync_SameProjectJobs_DispatchesSerially()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid();
        var jobs = new[]
        {
            CreateJob(projectId),
            CreateJob(projectId)
        };

        var services = new ServiceCollection();
        services.AddSingleton<IJobStore>(new PrefetchOnceJobStore(jobs));
        await using var serviceProvider = services.BuildServiceProvider();

        var workerPool = new BlockingWorkerPool();
        var scheduler = new JobScheduler(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            workerPool,
            new JobDomainEventDispatcher(),
            Options.Create(new JobSchedulerOptions
            {
                PrefetchInterval = TimeSpan.FromMilliseconds(50),
                PrefetchWindow = TimeSpan.FromMinutes(10),
                DispatchRetryDelay = TimeSpan.FromMilliseconds(50)
            }),
            NullLogger<JobScheduler>.Instance);

        using var schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var schedulerTask = scheduler.RunAsync(schedulerCancellation.Token);

        await workerPool.FirstDispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(
            () => workerPool.SecondDispatchStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken));

        workerPool.ReleaseFirstDispatch.SetResult();

        await workerPool.SecondDispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await schedulerCancellation.CancelAsync();

        await schedulerTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static Job CreateJob(Guid projectId)
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "test job",
            Prompt = "run",
            TriggerType = TriggerType.Once,
            TriggerValue = DateTimeOffset.UtcNow.ToString("O"),
            NextRunTime = DateTimeOffset.UtcNow.AddMilliseconds(-10),
            Status = JobStatus.Pending,
            IsEnabled = true
        };
    }

    private sealed class PrefetchOnceJobStore : IJobStore
    {
        private readonly IReadOnlyList<Job> _jobs;
        private bool _prefetched;

        public PrefetchOnceJobStore(IReadOnlyList<Job> jobs)
        {
            _jobs = jobs;
        }

        public Task<IReadOnlyList<Job>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken)
        {
            if (_prefetched)
            {
                return Task.FromResult<IReadOnlyList<Job>>([]);
            }

            _prefetched = true;
            return Task.FromResult(_jobs);
        }

        public Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task MarkSucceededAsync(Guid jobId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task MarkRetryAsync(Guid jobId, DateTimeOffset nextRunTime, int retryCount, string errorMessage, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task MarkFailedAsync(Guid jobId, int retryCount, string errorMessage, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task AddExecutionLogAsync(Guid jobId, Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingWorkerPool : IJobWorkerPool
    {
        private readonly JobWorkerDescriptor _worker = new(
            "worker-1",
            "node-1",
            "local",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Environment.ProcessorCount);

        private int _dispatchCount;

        public TaskCompletionSource FirstDispatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDispatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RegisterAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string workerId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JobWorkerDescriptor>> ListAvailableWorkersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<JobWorkerDescriptor>>([_worker]);
        }

        public async Task<JobWorkerDispatchResult> DispatchAsync(JobWorkerDescriptor worker, InMemoryJob job, CancellationToken cancellationToken)
        {
            var dispatchNumber = Interlocked.Increment(ref _dispatchCount);
            if (dispatchNumber == 1)
            {
                FirstDispatchStarted.SetResult();
                await ReleaseFirstDispatch.Task.WaitAsync(cancellationToken);
            }
            else if (dispatchNumber == 2)
            {
                SecondDispatchStarted.SetResult();
            }

            return new JobWorkerDispatchResult(worker.WorkerId, job.JobId);
        }
    }
}
