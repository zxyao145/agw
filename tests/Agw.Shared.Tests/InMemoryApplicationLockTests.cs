using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;

namespace Agw.Shared.Tests;

public sealed class InMemoryApplicationLockTests
{
    [Fact]
    public async Task AcquireAsync_SameResource_SerializesLeases()
    {
        var applicationLock = new InMemoryApplicationLock();
        var firstLease = await applicationLock.AcquireAsync("resource", TestContext.Current.CancellationToken);
        var secondLeaseTask = applicationLock.AcquireAsync("resource", TestContext.Current.CancellationToken);

        Assert.False(secondLeaseTask.IsCompleted);

        await firstLease.DisposeAsync();
        await using var secondLease = await secondLeaseTask;
    }

    [Fact]
    public async Task AcquireAsync_DifferentResources_DoesNotBlock()
    {
        var applicationLock = new InMemoryApplicationLock();
        await using var firstLease = await applicationLock.AcquireAsync("first", TestContext.Current.CancellationToken);

        await using var secondLease = await applicationLock.AcquireAsync(
            "second",
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task TryAcquireExclusiveAsync_SharedLeaseHeld_ReturnsNull()
    {
        var applicationLock = new InMemoryApplicationLock();
        await using var shared = await applicationLock.AcquireSharedAsync(
            "resource",
            TestContext.Current.CancellationToken
        );

        Assert.Null(await applicationLock.TryAcquireExclusiveAsync("resource", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryAcquireExclusiveAsync_ExclusiveLeaseHeld_ReturnsNull()
    {
        var applicationLock = new InMemoryApplicationLock();
        var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
            await applicationLock.TryAcquireExclusiveAsync("resource", TestContext.Current.CancellationToken)
        );

        await using (exclusive)
        {
            Assert.Null(
                await applicationLock.TryAcquireExclusiveAsync("resource", TestContext.Current.CancellationToken)
            );
        }
    }

    [Fact]
    public async Task TryAcquireExclusiveAsync_AllLeasesReturned_ReturnsLease()
    {
        var applicationLock = new InMemoryApplicationLock();
        var firstShared = await applicationLock.AcquireSharedAsync("resource", TestContext.Current.CancellationToken);
        var secondShared = await applicationLock.AcquireSharedAsync("resource", TestContext.Current.CancellationToken);
        await firstShared.DisposeAsync();
        await secondShared.DisposeAsync();

        await using var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
            await applicationLock.TryAcquireExclusiveAsync("resource", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task AcquireSharedAsync_ExclusiveLeaseHeld_WaitsForRelease()
    {
        var applicationLock = new InMemoryApplicationLock();
        var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
            await applicationLock.TryAcquireExclusiveAsync("resource", TestContext.Current.CancellationToken)
        );
        var sharedTask = applicationLock.AcquireSharedAsync("resource", TestContext.Current.CancellationToken);

        Assert.False(sharedTask.IsCompleted);
        await exclusive.DisposeAsync();
        await using var shared = await sharedTask;
    }

    [Fact]
    public async Task AcquireSharedAsync_MultipleShared_AllGranted()
    {
        var applicationLock = new InMemoryApplicationLock();
        var firstTask = applicationLock.AcquireSharedAsync("resource", TestContext.Current.CancellationToken);
        var secondTask = applicationLock.AcquireSharedAsync("resource", TestContext.Current.CancellationToken);

        Assert.True(firstTask.IsCompleted);
        Assert.True(secondTask.IsCompleted);
        await using var first = await firstTask;
        await using var second = await secondTask;
    }
}
