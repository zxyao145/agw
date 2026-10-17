using Agw.Shared.Coordination;

namespace Agw.Shared.Tests;

public class ApplicationLockScopeTests
{
    [Fact]
    public async Task DisposeAsync_CanceledScope_ReleasesResourceForNextCaller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var applicationLock = new InMemoryApplicationLock();
        var lease = await applicationLock.AcquireAsync("scope-test", cancellationToken);
        await using (var scope = new ApplicationLockScope(lease, request.Token))
        {
            Assert.False(scope.Token.IsCancellationRequested);
            await request.CancelAsync();
            Assert.True(scope.Token.IsCancellationRequested);
            using var waiter = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waiting = applicationLock.AcquireAsync("scope-test", waiter.Token);
            Assert.False(waiting.IsCompleted);
            await waiter.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        await using var next = await applicationLock
            .AcquireAsync("scope-test", cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.False(next.HandleLostToken.IsCancellationRequested);
    }
}
