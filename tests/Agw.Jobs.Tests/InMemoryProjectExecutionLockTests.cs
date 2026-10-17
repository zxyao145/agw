using Agw.Infrastructure.Jobs;

namespace Agw.Jobs.Tests;

public class InMemoryProjectExecutionLockTests
{
    [Fact]
    public async Task AcquireAsync_InMemoryLease_ReturnsNonCancelableHandleLostToken()
    {
        var projectLock = new InMemoryProjectExecutionLock();

        await using var lease = await projectLock.AcquireAsync(
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken
        );

        Assert.False(lease.HandleLostToken.CanBeCanceled);
    }

    [Fact]
    public async Task AcquireAsync_WhenProjectIsLocked_WaitsUntilLeaseIsDisposed()
    {
        var projectLock = new InMemoryProjectExecutionLock();
        var projectId = Guid.CreateVersion7();
        var firstLease = await projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        var secondLeaseTask = projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        Assert.False(secondLeaseTask.IsCompleted);
        await firstLease.DisposeAsync();
        await using var secondLease = await secondLeaseTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task TryAcquireAsync_WhenProjectIsLocked_ReturnsWithoutWaiting()
    {
        var projectLock = new InMemoryProjectExecutionLock();
        var projectId = Guid.CreateVersion7();
        await using var firstLease = await projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        var secondLease = await projectLock.TryAcquireAsync(projectId, TestContext.Current.CancellationToken);

        Assert.Null(secondLease);
    }

    [Fact]
    public async Task AcquireAsync_WhenProjectsDiffer_DoesNotBlock()
    {
        var projectLock = new InMemoryProjectExecutionLock();
        await using var firstLease = await projectLock.AcquireAsync(
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken
        );

        await using var secondLease = await projectLock
            .AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }
}
