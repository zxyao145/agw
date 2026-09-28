namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 按完整路径哈希选取的写锁，同一进程内不同实例（包括嵌套根目录）对同一文件的写入与读改写操作互斥。
/// Write locks selected by full-path hash, so writes and read-modify-write operations on the same file are mutually
/// exclusive across instances in the process, including nested roots.
/// </summary>
internal static class LocalFilePathLocks
{
    private static readonly SemaphoreSlim[] PathLocks = Enumerable
        .Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    private static readonly StringComparer PathLockComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static async Task<IDisposable> AcquireAsync(string fullPath, CancellationToken ct)
    {
        var semaphore = PathLocks[(int)((uint)PathLockComparer.GetHashCode(fullPath) % (uint)PathLocks.Length)];
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new PathLock(semaphore);
    }

    /// <summary>
    /// 已获取的路径写锁，释放时归还信号量。
    /// An acquired path write lock that returns its semaphore when disposed.
    /// </summary>
    private sealed class PathLock : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public PathLock(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
