using System.Collections.Concurrent;

using Agw.Jobs.Scheduling.Coordination;

namespace Agw.Infrastructure.Jobs;

public sealed class InMemoryProjectExecutionLock : IProjectExecutionLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
