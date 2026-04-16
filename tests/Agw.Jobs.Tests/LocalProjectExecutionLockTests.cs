using Agw.Jobs.External.StandAlone;

namespace Agw.Jobs.Tests;

public class LocalProjectExecutionLockTests
{
    [Fact]
    public async Task AcquireAsync_WhenSameProjectLockHeld_WaitsUntilLeaseDisposed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid();
        var executionLock = new LocalProjectExecutionLock();

        await using var firstLease = await executionLock.AcquireAsync(projectId, cancellationToken);

        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(async () =>
        {
            await using var secondLease = await executionLock.AcquireAsync(projectId, cancellationToken);
            secondAcquired.SetResult();
            await releaseSecond.Task.WaitAsync(cancellationToken);
        }, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        Assert.False(secondAcquired.Task.IsCompleted);

        await firstLease.DisposeAsync();

        await secondAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        releaseSecond.SetResult();
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    [Fact]
    public async Task AcquireAsync_WhenDifferentProjectLockHeld_AcquiresImmediately()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstProjectId = Guid.NewGuid();
        var secondProjectId = Guid.NewGuid();
        var executionLock = new LocalProjectExecutionLock();

        await using var firstLease = await executionLock.AcquireAsync(firstProjectId, cancellationToken);

        await using var secondLease = await executionLock
            .AcquireAsync(secondProjectId, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
