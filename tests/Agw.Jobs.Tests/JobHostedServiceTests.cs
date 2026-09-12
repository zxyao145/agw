using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Runtime;
using Agw.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Jobs.Tests;

public sealed class JobHostedServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispatch_InfrastructureFailure_NextJobInProjectCanRun(bool lockFailure)
    {
        using var services = CreateServices(new ClaimStore { FailNext = !lockFailure });
        var projectLock = new TestLock { FailNext = lockFailure };
        using var scheduler = CreateScheduler(services, projectLock, TimeProvider.System);
        var first = new ScheduledJob { JobId = Guid.NewGuid(), ProjectId = Guid.NewGuid() };
        Invoke(scheduler, "DispatchOrQueueByProject", first, TestContext.Current.CancellationToken);
        var running = (ConcurrentDictionary<Guid, Task>)Field(scheduler, "_runningExecutions");
        try
        {
            await Task.WhenAll(running.Values);
        }
        catch (IOException) { }

        Invoke(
            scheduler,
            "DispatchOrQueueByProject",
            first with
            {
                JobId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, projectLock.Attempts);
    }

    [Fact]
    public async Task RunExecuteLoop_TimerWins_NextDueJobNeedsOnlyOneWake()
    {
        var clock = new ManualTimeProvider();
        var store = new ClaimStore();
        using var services = CreateServices(store);
        using var scheduler = CreateScheduler(services, new TestLock(), clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new ScheduledJob
        {
            JobId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            NextRunTime = clock.GetUtcNow().AddSeconds(10),
        };
        Invoke(scheduler, "UpsertScheduledJob", first);
        ((SemaphoreSlim)Field(scheduler, "_wakeSignal")).Wait(0, TestContext.Current.CancellationToken);
        var loop = (Task)Invoke(scheduler, "RunExecuteLoopAsync", cancellation.Token)!;
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(10), cancellation.Token);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(
                first.JobId,
                await store
                    .Claims.Reader.ReadAsync(cancellation.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token)
            );
            var second = first with { JobId = Guid.NewGuid(), NextRunTime = clock.GetUtcNow() };

            Invoke(scheduler, "UpsertScheduledJob", second);

            Assert.Equal(
                second.JobId,
                await store
                    .Claims.Reader.ReadAsync(cancellation.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token)
            );
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await loop;
            }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task StopAsync_RunningQueue_WaitsForExecutionCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ClaimStore
        {
            Jobs = [new Job { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() }],
            OnClaim = async cancellation =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }
                finally
                {
                    cleanupStarted.TrySetResult();
                    await release.Task;
                }
            },
        };
        using var services = CreateServices(store);
        using var scheduler = CreateScheduler(services, new TestLock(), TimeProvider.System);
        await scheduler.StartAsync(token);
        await store.Claims.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
        var stopping = scheduler.StopAsync(token);
        try
        {
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2), token);
        }
    }

    private sealed class InitializedState : IServerInitializationState
    {
        public bool IsInitialized => true;
    }

    private static ServiceProvider CreateServices(ClaimStore store) =>
        new ServiceCollection()
            .AddSingleton<IJobStore>(store)
            .AddScoped(provider => new JobAttemptRunner(
                provider.GetRequiredService<IJobStore>(),
                null!,
                null!,
                NullLogger<JobAttemptRunner>.Instance
            ))
            .BuildServiceProvider();

    private static JobHostedService CreateScheduler(
        ServiceProvider services,
        TestLock projectLock,
        TimeProvider clock
    ) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JobHostedService>.Instance,
            new JobSchedulerWakeSignal(clock),
            projectLock,
            new InitializedState(),
            clock
        );

    private static object Field(object instance, string name) =>
        typeof(JobHostedService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object[] args) =>
        typeof(JobHostedService)
            .GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                args.Select(arg => arg.GetType()).ToArray(),
                null
            )!
            .Invoke(instance, args);

    private sealed class ClaimStore : IJobStore
    {
        public Channel<Guid> Claims { get; } = Channel.CreateUnbounded<Guid>();
        public bool FailNext { get; set; }
        public IReadOnlyList<Job> Jobs { get; set; } = [];
        public Func<CancellationToken, Task>? OnClaim { get; set; }

        public Task<IReadOnlyList<Job>> PrefetchAsync(
            DateTimeOffset now,
            DateTimeOffset horizon,
            CancellationToken cancellationToken
        ) => Task.FromResult(Jobs);

        public async Task<JobAttemptClaim?> TryStartAttemptAsync(Guid jobId, CancellationToken cancellationToken)
        {
            Claims.Writer.TryWrite(jobId);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Simulated claim failure");
            }
            if (OnClaim != null)
                await OnClaim(cancellationToken);
            return null;
        }
    }

    private sealed class TestLock : IProjectExecutionLock
    {
        public bool FailNext { get; set; }
        public int Attempts { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
        {
            Attempts++;
            if (FailNext)
            {
                FailNext = false;
                return Task.FromException<IAsyncDisposable>(new IOException("Simulated lock outage"));
            }
            return Task.FromResult<IAsyncDisposable>(new Lease());
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
