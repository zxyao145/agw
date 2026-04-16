using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;
using Agw.Jobs.Executors.StandAlone;

using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Jobs.Tests;

public class LocalJobWorkerPoolTests
{
    [Fact]
    public async Task RegisterAsync_ListAvailableWorkersAsync_ReturnsRegisteredWorker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new LocalJobWorkerPool(new RecordingJobWorker(), NullLogger<LocalJobWorkerPool>.Instance);
        var worker = CreateWorker("worker-1");

        await pool.RegisterAsync(worker, cancellationToken);

        var workers = await pool.ListAvailableWorkersAsync(cancellationToken);
        var registeredWorker = Assert.Single(workers);
        Assert.Equal(worker.WorkerId, registeredWorker.WorkerId);
        Assert.Equal(worker.NodeId, registeredWorker.NodeId);
    }

    [Fact]
    public async Task DispatchAsync_SelectedWorker_ExecutesJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var worker = new RecordingJobWorker();
        var pool = new LocalJobWorkerPool(worker, NullLogger<LocalJobWorkerPool>.Instance);
        var descriptor = CreateWorker("worker-1");
        var job = new InMemoryJob
        {
            JobId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "test job",
            NextRunTime = DateTimeOffset.UtcNow
        };

        await pool.RegisterAsync(descriptor, cancellationToken);
        var result = await pool.DispatchAsync(descriptor, job, cancellationToken);

        Assert.Equal(descriptor.WorkerId, result.WorkerId);
        Assert.Equal(job.JobId, result.JobId);
        Assert.Equal(job.JobId, Assert.Single(worker.ExecutedJobIds));
    }

    private static JobWorkerDescriptor CreateWorker(string workerId)
    {
        return new JobWorkerDescriptor(
            workerId,
            "node-1",
            "local",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Environment.ProcessorCount);
    }

    private sealed class RecordingJobWorker : IJobWorker
    {
        public List<Guid> ExecutedJobIds { get; } = [];

        public Task<JobWorkerExecutionResult> ExecuteAsync(InMemoryJob job, CancellationToken cancellationToken)
        {
            ExecutedJobIds.Add(job.JobId);
            return Task.FromResult(JobWorkerExecutionResult.Remove(job.JobId));
        }
    }
}
