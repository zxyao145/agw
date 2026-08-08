using Agw.Shared.Coordination;

namespace Agw.Shared.Tests;

public sealed class InMemoryApplicationLockTests
{
    [Fact]
    public async Task AcquireAsync_SameResource_SerializesLeases()
    {
        var applicationLock = new InMemoryApplicationLock();
        var firstLease = await applicationLock.AcquireAsync(
            "resource",
            TestContext.Current.CancellationToken);
        var secondLeaseTask = applicationLock.AcquireAsync(
            "resource",
            TestContext.Current.CancellationToken);

        Assert.False(secondLeaseTask.IsCompleted);

        await firstLease.DisposeAsync();
        await using var secondLease = await secondLeaseTask;
    }

    [Fact]
    public async Task AcquireAsync_DifferentResources_DoesNotBlock()
    {
        var applicationLock = new InMemoryApplicationLock();
        await using var firstLease = await applicationLock.AcquireAsync(
            "first",
            TestContext.Current.CancellationToken);

        await using var secondLease = await applicationLock.AcquireAsync(
            "second",
            TestContext.Current.CancellationToken);
    }
}
