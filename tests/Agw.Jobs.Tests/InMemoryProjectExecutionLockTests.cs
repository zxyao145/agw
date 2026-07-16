using Agw.Infrastructure.Jobs;

namespace Agw.Jobs.Tests;

public class InMemoryProjectExecutionLockTests
{
    [Fact]
    public async Task AcquireAsync_WhenProjectIsLocked_WaitsUntilLeaseIsDisposed()
    {
        var projectLock = new InMemoryProjectExecutionLock();
        var projectId = Guid.CreateVersion7();
        var firstLease = await projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        var secondLeaseTask = projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        Assert.False(secondLeaseTask.IsCompleted);
        await firstLease.DisposeAsync();
        await using var secondLease = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcquireAsync_WhenProjectsDiffer_DoesNotBlock()
    {
        var projectLock = new InMemoryProjectExecutionLock();
        await using var firstLease = await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        await using var secondLease = await projectLock
            .AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }
}
