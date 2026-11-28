using System.Collections.Concurrent;
using Agw.Shared.Contracts.Coordination;
using Nito.AsyncEx;

namespace Agw.Shared.Coordination;

public sealed class InMemoryApplicationLock : IApplicationLock
{
    public static InMemoryApplicationLock Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Entry> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderWriterEntry> _readerWriterLocks = new(StringComparer.Ordinal);

    public async Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
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

    public async Task<IApplicationLockLease> AcquireSharedAsync(
        string resourceName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var entry = AddReaderWriterReference(resourceName);
        try
        {
            var handle = await entry.Lock.ReaderLockAsync(cancellationToken).ConfigureAwait(false);
            return new ReaderWriterLease(this, resourceName, entry, handle);
        }
        catch
        {
            ReleaseReaderWriter(resourceName, entry, null);
            throw;
        }
    }

    public async Task<IApplicationLockLease?> TryAcquireExclusiveAsync(
        string resourceName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = AddReaderWriterReference(resourceName);
        try
        {
            var handle = await entry.Lock.WriterLockAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
            return new ReaderWriterLease(this, resourceName, entry, handle);
        }
        catch (OperationCanceledException)
        {
            ReleaseReaderWriter(resourceName, entry, null);
            return null;
        }
        catch
        {
            ReleaseReaderWriter(resourceName, entry, null);
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

    private ReaderWriterEntry AddReaderWriterReference(string resourceName)
    {
        while (true)
        {
            var entry = _readerWriterLocks.GetOrAdd(resourceName, static _ => new ReaderWriterEntry());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
                return entry;
            }
        }
    }

    private void ReleaseReaderWriter(string resourceName, ReaderWriterEntry entry, IDisposable? handle)
    {
        handle?.Dispose();

        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
            {
                return;
            }

            entry.Retired = true;
            _readerWriterLocks.TryRemove(new KeyValuePair<string, ReaderWriterEntry>(resourceName, entry));
        }
    }

    private sealed class Entry
    {
        public object SyncRoot { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class ReaderWriterEntry
    {
        public object SyncRoot { get; } = new();

        public AsyncReaderWriterLock Lock { get; } = new();

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Lease : IApplicationLockLease
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

        public CancellationToken HandleLostToken => CancellationToken.None;

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

    private sealed class ReaderWriterLease : IApplicationLockLease
    {
        private readonly InMemoryApplicationLock _owner;
        private readonly string _resourceName;
        private readonly ReaderWriterEntry _entry;
        private IDisposable? _handle;

        public ReaderWriterLease(
            InMemoryApplicationLock owner,
            string resourceName,
            ReaderWriterEntry entry,
            IDisposable handle
        )
        {
            _owner = owner;
            _resourceName = resourceName;
            _entry = entry;
            _handle = handle;
        }

        public CancellationToken HandleLostToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle != null)
            {
                _owner.ReleaseReaderWriter(_resourceName, _entry, handle);
            }

            return ValueTask.CompletedTask;
        }
    }
}
