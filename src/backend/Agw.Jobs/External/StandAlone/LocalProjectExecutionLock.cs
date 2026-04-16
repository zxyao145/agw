using System.Collections.Concurrent;

namespace Agw.Jobs.External.StandAlone;

public sealed class LocalProjectExecutionLock : IProjectExecutionLock
{
    private readonly ConcurrentDictionary<Guid, LockState> _locks = new();

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        LockState lockState;
        while (true)
        {
            lockState = _locks.GetOrAdd(projectId, static _ => new LockState());
            if (lockState.TryAddReference())
            {
                break;
            }

            _locks.TryRemove(new KeyValuePair<Guid, LockState>(projectId, lockState));
        }

        try
        {
            await lockState.Semaphore.WaitAsync(cancellationToken);
            return new LockLease(this, projectId, lockState);
        }
        catch
        {
            ReleaseReference(projectId, lockState);
            throw;
        }
    }

    private void Release(Guid projectId, LockState lockState)
    {
        lockState.Semaphore.Release();
        ReleaseReference(projectId, lockState);
    }

    private void ReleaseReference(Guid projectId, LockState lockState)
    {
        if (lockState.ReleaseReference())
        {
            _locks.TryRemove(new KeyValuePair<Guid, LockState>(projectId, lockState));
        }
    }

    private sealed class LockState
    {
        private readonly object _syncRoot = new();
        private int _referenceCount;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_syncRoot)
            {
                if (_retired)
                {
                    return false;
                }

                _referenceCount++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_syncRoot)
            {
                _referenceCount--;
                if (_referenceCount > 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private sealed class LockLease(
        LocalProjectExecutionLock owner,
        Guid projectId,
        LockState lockState) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(projectId, lockState);
            }

            return ValueTask.CompletedTask;
        }
    }
}
