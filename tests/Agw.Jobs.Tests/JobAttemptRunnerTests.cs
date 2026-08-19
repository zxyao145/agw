using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Jobs.Tests;

public class JobAttemptRunnerTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_RecurringJobSucceeds_ReturnsRescheduleAndWritesSuccessLog()
    {
        var store = new RecordingJobStore { MarkRunningResult = true };
        var taskId = Guid.CreateVersion7();
        var executor = new StubJobAgentExecutor(taskId);
        var runner = CreateRunner(store, executor);
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00");

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
        Assert.Equal(UtcNow.AddMinutes(15), reschedule.Job.NextRunTime);
        Assert.Equal(0, reschedule.Job.RetryCount);
        Assert.Equal(UtcNow.AddMinutes(15), store.SucceededNextRunTime);
        Assert.Equal(taskId, store.LastLog?.TaskId);
        Assert.True(store.LastLog?.Success);
        Assert.Equal(1, store.LastLog?.Attempt);
    }

    [Fact]
    public async Task RunAsync_OnceJobSucceeds_ReturnsDropAndMarksSucceededWithoutNextRun()
    {
        var store = new RecordingJobStore { MarkRunningResult = true };
        var runner = CreateRunner(store, new StubJobAgentExecutor(Guid.CreateVersion7()));
        var scheduledJob = CreateScheduledJob(TriggerType.Once, UtcNow.ToString("O"));

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.True(store.MarkSucceededCalled);
        Assert.Null(store.SucceededNextRunTime);
        Assert.True(store.LastLog?.Success);
    }

    [Fact]
    public async Task RunAsync_JobCannotBeClaimed_ReturnsDropWithoutExecutingOrLogging()
    {
        var store = new RecordingJobStore { MarkRunningResult = false };
        var executor = new StubJobAgentExecutor(Guid.CreateVersion7());
        var runner = CreateRunner(store, executor);

        var result = await runner.RunAsync(
            CreateScheduledJob(TriggerType.Interval, "00:15:00"),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Null(store.LastLog);
    }

    [Fact]
    public async Task RunAsync_FirstFailure_ReturnsRetryAndWritesFailedLog()
    {
        var store = new RecordingJobStore { MarkRunningResult = true };
        var runner = CreateRunner(store, new StubJobAgentExecutor(new InvalidOperationException("boom")));
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00", retryCount: 0, maxRetryCount: 3);

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
        Assert.Equal(UtcNow.AddSeconds(30), reschedule.Job.NextRunTime);
        Assert.Equal(1, reschedule.Job.RetryCount);
        Assert.Equal((1, UtcNow.AddSeconds(30), "boom"), store.Retry);
        Assert.Equal(Guid.Empty, store.LastLog?.TaskId);
        Assert.False(store.LastLog?.Success);
        Assert.Equal(1, store.LastLog?.Attempt);
    }

    [Fact]
    public async Task RunAsync_RetriesExhausted_ReturnsDropAndMarksFailed()
    {
        var store = new RecordingJobStore { MarkRunningResult = true };
        var runner = CreateRunner(store, new StubJobAgentExecutor(new InvalidOperationException("boom")));
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00", retryCount: 3, maxRetryCount: 3);

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal((4, "boom"), store.Failure);
        Assert.False(store.LastLog?.Success);
        Assert.Equal(4, store.LastLog?.Attempt);
    }

    [Fact]
    public async Task RunAsync_JobNoLongerExists_ReturnsDropWithoutBookkeeping()
    {
        var store = new RecordingJobStore { MarkRunningException = new AgwException(ErrorCodes.JobNotFound) };
        var executor = new StubJobAgentExecutor(Guid.CreateVersion7());
        var runner = CreateRunner(store, executor);

        var result = await runner.RunAsync(
            CreateScheduledJob(TriggerType.Interval, "00:15:00"),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Null(store.Retry);
        Assert.Null(store.Failure);
        Assert.Null(store.LastLog);
    }

    private static JobAttemptRunner CreateRunner(RecordingJobStore store, IJobAgentExecutor executor)
    {
        return new JobAttemptRunner(
            store,
            executor,
            new JobScheduleCalculator(),
            new TestTimeProvider(UtcNow),
            NullLogger<JobAttemptRunner>.Instance
        );
    }

    private static ScheduledJob CreateScheduledJob(
        TriggerType triggerType,
        string triggerValue,
        int retryCount = 0,
        int maxRetryCount = 3
    )
    {
        return new ScheduledJob
        {
            JobId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.CreateVersion7(),
            Name = "scheduled job",
            Prompt = "run",
            TriggerType = triggerType,
            TriggerValue = triggerValue,
            NextRunTime = UtcNow,
            RetryCount = retryCount,
            MaxRetryCount = maxRetryCount,
            Version = 1,
        };
    }

    private sealed class StubJobAgentExecutor : IJobAgentExecutor
    {
        private readonly Guid _taskId;
        private readonly Exception? _exception;

        public StubJobAgentExecutor(Guid taskId)
        {
            _taskId = taskId;
        }

        public StubJobAgentExecutor(Exception exception)
        {
            _exception = exception;
        }

        public int ExecuteCount { get; private set; }

        public Task<Guid> ExecuteAsync(Job job, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            if (_exception != null)
            {
                throw _exception;
            }

            return Task.FromResult(_taskId);
        }
    }

    private sealed class RecordingJobStore : IJobStore
    {
        public bool MarkRunningResult { get; init; }
        public Exception? MarkRunningException { get; init; }
        public bool MarkSucceededCalled { get; private set; }
        public DateTimeOffset? SucceededNextRunTime { get; private set; }
        public (int RetryCount, DateTimeOffset NextRunTime, string Error)? Retry { get; private set; }
        public (int RetryCount, string Error)? Failure { get; private set; }
        public ExecutionLogCall? LastLog { get; private set; }

        public Task<IReadOnlyList<Job>> PrefetchAsync(
            DateTimeOffset now,
            DateTimeOffset horizon,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult<IReadOnlyList<Job>>([]);
        }

        public Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken)
        {
            if (MarkRunningException != null)
            {
                throw MarkRunningException;
            }

            return Task.FromResult(MarkRunningResult);
        }

        public Task MarkSucceededAsync(Guid jobId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
        {
            MarkSucceededCalled = true;
            SucceededNextRunTime = nextRunTime;
            return Task.CompletedTask;
        }

        public Task MarkRetryAsync(
            Guid jobId,
            DateTimeOffset nextRunTime,
            int retryCount,
            string errorMessage,
            CancellationToken cancellationToken
        )
        {
            Retry = (retryCount, nextRunTime, errorMessage);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid jobId,
            int retryCount,
            string errorMessage,
            CancellationToken cancellationToken
        )
        {
            Failure = (retryCount, errorMessage);
            return Task.CompletedTask;
        }

        public Task AddExecutionLogAsync(
            Guid jobId,
            Guid taskId,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            bool success,
            int attempt,
            string? errorMessage,
            CancellationToken cancellationToken
        )
        {
            LastLog = new ExecutionLogCall(taskId, success, attempt, errorMessage);
            return Task.CompletedTask;
        }
    }

    private sealed record ExecutionLogCall(Guid TaskId, bool Success, int Attempt, string? ErrorMessage);
}
