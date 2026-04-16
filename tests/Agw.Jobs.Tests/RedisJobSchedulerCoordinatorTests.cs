using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Cluster;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using StackExchange.Redis;

namespace Agw.Jobs.Tests;

public class RedisJobSchedulerCoordinatorTests
{
    [Fact]
    public async Task RunAsync_WhenSchedulerLockRenewalFails_CancelsScheduler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        connectionMultiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                When.NotExists)
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)0, ResultType.Integer)));

        var schedulerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RedisJobSchedulerCoordinator(
            connectionMultiplexer,
            Options.Create(new JobWorkerPoolOptions
            {
                SchedulerLockTtl = TimeSpan.FromSeconds(5),
                SchedulerLockRetryDelay = TimeSpan.FromMilliseconds(10),
                SchedulerLockRenewInterval = TimeSpan.FromMilliseconds(10)
            }),
            NullLogger<RedisJobSchedulerCoordinator>.Instance);

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = coordinator.RunAsync(
            async schedulerCancellationToken =>
            {
                schedulerStarted.TrySetResult();
                await using var registration = schedulerCancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    schedulerCancelled);
                await Task.Delay(Timeout.InfiniteTimeSpan, schedulerCancellationToken);
            },
            runCancellation.Token);

        await schedulerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await schedulerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await runCancellation.CancelAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
