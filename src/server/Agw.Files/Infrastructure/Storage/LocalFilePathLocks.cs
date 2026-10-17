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
        var semaphore = PathLocks[IndexOf(fullPath)];
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new PathLock(semaphore);
    }

    /// <summary>
    /// 同时获取两个路径的写锁（例如移动的源与目标）：按信号量序号依次获取，两个路径落在同一个信号量时只获取一次，因此方向
    /// 相反的两个并发移动不会彼此无限等待。
    /// Acquires the write locks of two paths at once (for example the source and destination of a move): semaphores are
    /// taken in index order, and only once when both paths share a semaphore, so two concurrent moves in opposite
    /// directions can never wait on each other forever.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        string firstFullPath,
        string secondFullPath,
        CancellationToken ct
    )
    {
        var first = IndexOf(firstFullPath);
        var second = IndexOf(secondFullPath);
        if (first == second)
        {
            return await AcquireAsync(firstFullPath, ct).ConfigureAwait(false);
        }

        var lower = PathLocks[Math.Min(first, second)];
        var higher = PathLocks[Math.Max(first, second)];
        await lower.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await higher.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lower.Release();
            throw;
        }

        return new PathLock(lower, higher);
    }

    private static int IndexOf(string fullPath) =>
        (int)((uint)PathLockComparer.GetHashCode(fullPath) % (uint)PathLocks.Length);

    /// <summary>
    /// 已获取的路径写锁，释放时归还信号量。
    /// An acquired path write lock that returns its semaphores when disposed.
    /// </summary>
    private sealed class PathLock : IDisposable
    {
        private SemaphoreSlim[]? _semaphores;

        public PathLock(params SemaphoreSlim[] semaphores)
        {
            _semaphores = semaphores;
        }

        public void Dispose()
        {
            var semaphores = Interlocked.Exchange(ref _semaphores, null);
            if (semaphores == null)
            {
                return;
            }

            for (var index = semaphores.Length - 1; index >= 0; index--)
            {
                semaphores[index].Release();
            }
        }
    }
}
