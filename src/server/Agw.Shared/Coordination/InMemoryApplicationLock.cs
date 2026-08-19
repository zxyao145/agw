using System.Collections.Concurrent;
using Agw.Shared.Contracts.Coordination;

namespace Agw.Shared.Coordination;

public sealed class InMemoryApplicationLock : IApplicationLock
{
    public static InMemoryApplicationLock Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Entry> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(string resourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        Entry entry;
        while (true)
        {
            entry = _locks.GetOrAdd(resourceName, static _ => new Entry());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
                break;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, resourceName, entry);
        }
        catch
        {
            Release(resourceName, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void Release(string resourceName, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
            {
                return;
            }

            entry.Retired = true;
            _locks.TryRemove(new KeyValuePair<string, Entry>(resourceName, entry));
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public object SyncRoot { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly InMemoryApplicationLock _owner;
        private readonly string _resourceName;
        private Entry? _entry;

        public Lease(InMemoryApplicationLock owner, string resourceName, Entry entry)
        {
            _owner = owner;
            _resourceName = resourceName;
            _entry = entry;
        }

        public ValueTask DisposeAsync()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry != null)
            {
                _owner.Release(_resourceName, entry, releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
