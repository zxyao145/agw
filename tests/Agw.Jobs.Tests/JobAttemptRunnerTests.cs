using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Jobs.Tests;

public class JobAttemptRunnerTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_RecurringJobSucceeds_ReturnsRescheduleAndWritesSuccessLog()
    {
        var store = new RecordingJobStore { CanClaim = true };
        var executor = new StubJobAgentExecutor();
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00");
        var expected = new JobAttemptResult.Reschedule(
            scheduledJob with
            {
                NextRunTime = UtcNow.AddMinutes(15),
                RetryCount = 0,
            }
        );
        var recorder = new RecordingOutcomeRecorder(expected);
        var runner = CreateRunner(store, executor, recorder);

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
        Assert.Equal(UtcNow.AddMinutes(15), reschedule.Job.NextRunTime);
        Assert.Equal(0, reschedule.Job.RetryCount);
        Assert.Equal(store.ExecutionId, executor.ExecutionId);
        Assert.Equal((scheduledJob.JobId, store.ExecutionId, true, null), recorder.LastCall);
    }

    [Fact]
    public async Task RunAsync_OnceJobSucceeds_ReturnsDropAndMarksSucceededWithoutNextRun()
    {
        var store = new RecordingJobStore { CanClaim = true };
        var recorder = new RecordingOutcomeRecorder(new JobAttemptResult.Drop());
        var runner = CreateRunner(store, new StubJobAgentExecutor(), recorder);
        var scheduledJob = CreateScheduledJob(TriggerType.Once, UtcNow.ToString("O"));

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.True(recorder.LastCall?.Success);
    }

    [Fact]
    public async Task RunAsync_JobCannotBeClaimed_ReturnsDropWithoutExecutingOrLogging()
    {
        var store = new RecordingJobStore { CanClaim = false };
        var executor = new StubJobAgentExecutor();
        var recorder = new RecordingOutcomeRecorder(new JobAttemptResult.Drop());
        var runner = CreateRunner(store, executor, recorder);

        var result = await runner.RunAsync(
            CreateScheduledJob(TriggerType.Interval, "00:15:00"),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Null(recorder.LastCall);
    }

    [Fact]
    public async Task RunAsync_FirstFailure_ReturnsRetryAndWritesFailedLog()
    {
        var store = new RecordingJobStore { CanClaim = true };
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00", retryCount: 0, maxRetryCount: 3);
        var expected = new JobAttemptResult.Reschedule(
            scheduledJob with
            {
                NextRunTime = UtcNow.AddSeconds(30),
                RetryCount = 1,
            }
        );
        var recorder = new RecordingOutcomeRecorder(expected);
        var runner = CreateRunner(store, new StubJobAgentExecutor(new InvalidOperationException("boom")), recorder);

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
        Assert.Equal(UtcNow.AddSeconds(30), reschedule.Job.NextRunTime);
        Assert.Equal(1, reschedule.Job.RetryCount);
        Assert.Equal((scheduledJob.JobId, store.ExecutionId, false, "boom"), recorder.LastCall);
    }

    [Fact]
    public async Task RunAsync_RetriesExhausted_ReturnsDropAndMarksFailed()
    {
        var store = new RecordingJobStore { CanClaim = true };
        var recorder = new RecordingOutcomeRecorder(new JobAttemptResult.Drop());
        var runner = CreateRunner(store, new StubJobAgentExecutor(new InvalidOperationException("boom")), recorder);
        var scheduledJob = CreateScheduledJob(TriggerType.Interval, "00:15:00", retryCount: 3, maxRetryCount: 3);

        var result = await runner.RunAsync(scheduledJob, TestContext.Current.CancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal((scheduledJob.JobId, store.ExecutionId, false, "boom"), recorder.LastCall);
    }

    [Fact]
    public async Task RunAsync_JobNoLongerExists_ReturnsDropWithoutBookkeeping()
    {
        var store = new RecordingJobStore { StartException = new AgwException(ErrorCodes.JobNotFound) };
        var executor = new StubJobAgentExecutor();
        var recorder = new RecordingOutcomeRecorder(new JobAttemptResult.Drop());
        var runner = CreateRunner(store, executor, recorder);

        var result = await runner.RunAsync(
            CreateScheduledJob(TriggerType.Interval, "00:15:00"),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<JobAttemptResult.Drop>(result);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Null(recorder.LastCall);
    }

    private static JobAttemptRunner CreateRunner(
        RecordingJobStore store,
        IJobAgentExecutor executor,
        IJobAttemptOutcomeRecorder recorder
    )
    {
        return new JobAttemptRunner(store, executor, recorder, NullLogger<JobAttemptRunner>.Instance);
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
        private readonly Exception? _exception;

        public StubJobAgentExecutor() { }

        public StubJobAgentExecutor(Exception exception)
        {
            _exception = exception;
        }

        public int ExecuteCount { get; private set; }
        public Guid? ExecutionId { get; private set; }

        public Task ExecuteAsync(Job job, Guid executionId, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            ExecutionId = executionId;
            if (_exception != null)
            {
                throw _exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJobStore : IJobStore
    {
        public bool CanClaim { get; init; }
        public Exception? StartException { get; init; }
        public Guid ExecutionId { get; } = Guid.CreateVersion7();

        public Task<IReadOnlyList<Job>> PrefetchAsync(
            DateTimeOffset now,
            DateTimeOffset horizon,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult<IReadOnlyList<Job>>([]);
        }

        public Task<JobAttemptClaim?> TryStartAttemptAsync(Guid jobId, CancellationToken cancellationToken)
        {
            if (StartException != null)
            {
                throw StartException;
            }

            JobAttemptClaim? claim = CanClaim
                ? new JobAttemptClaim(new Job { Id = jobId, Status = JobStatus.Running }, ExecutionId, UtcNow, 1)
                : null;
            return Task.FromResult(claim);
        }
    }

    private sealed class RecordingOutcomeRecorder : IJobAttemptOutcomeRecorder
    {
        private readonly JobAttemptResult _result;

        public RecordingOutcomeRecorder(JobAttemptResult result)
        {
            _result = result;
        }

        public (Guid JobId, Guid ExecutionId, bool Success, string? Error)? LastCall { get; private set; }

        public Task<JobAttemptResult> RecordAsync(
            Guid jobId,
            Guid executionId,
            bool success,
            string? errorMessage,
            CancellationToken cancellationToken
        )
        {
            LastCall = (jobId, executionId, success, errorMessage);
            return Task.FromResult(_result);
        }
    }
}
