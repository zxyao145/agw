using System.Runtime.Versioning;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;

namespace Agw.Files.Tests;

/// <summary>
/// FilePathLocks 的两种文件身份：内容操作解析路径每一段中的符号链接，指向同一个文件的不同路径共用一个锁；目录条目操作只按
/// 物理路径解析父目录，不解析最后一段链接的目标。等待期间物理路径变化时按新的物理路径重试。
/// The two file identities of FilePathLocks: content operations resolve symbolic links in every segment, so different
/// paths to one file share one lock; directory-entry operations resolve only the parent directory physically and never
/// resolve the target of the last segment. Acquisition retries with the new physical path when it changes while
/// waiting.
/// </summary>
public sealed class FilePathLocksTests : IDisposable
{
    private readonly string _root = Directory
        .CreateDirectory(Path.Combine(AppContext.BaseDirectory, "file-path-locks", Guid.CreateVersion7().ToString("N")))
        .FullName;

    public static bool IsUnix => !OperatingSystem.IsWindows();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AcquireContentAsync_LinkAndItsTarget_ShareOneLock()
    {
        var target = Path.Combine(_root, "real.md");
        var link = Path.Combine(_root, "link.md");
        await File.WriteAllTextAsync(target, "content", Token);
        File.CreateSymbolicLink(link, target);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        await AssertWaitsAsync(
            () => locks.AcquireContentAsync(link, Token),
            () => locks.AcquireContentAsync(target, Token)
        );
    }

    [Fact]
    public async Task AcquireContentAsync_MissingFileUnderDirectoryLink_SharesLockWithRealDirectory()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var linked = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linked, real);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        await AssertWaitsAsync(
            () => locks.AcquireContentAsync(Path.Combine(linked, "new.md"), Token),
            () => locks.AcquireContentAsync(Path.Combine(real, "new.md"), Token)
        );
    }

    [Fact]
    public async Task AcquireContentAsync_DanglingLinkAndItsMissingTarget_ShareOneLock()
    {
        var target = Path.Combine(_root, "created.md");
        var link = Path.Combine(_root, "link.md");
        File.CreateSymbolicLink(link, target);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        await AssertWaitsAsync(
            () => locks.AcquireContentAsync(link, Token),
            () => locks.AcquireContentAsync(target, Token)
        );
    }

    [Fact]
    public async Task AcquireEntryAsync_RegularFileWhileContentHeld_Waits()
    {
        var file = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(file, "content", Token);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        await AssertWaitsAsync(
            () => locks.AcquireContentAsync(file, Token),
            () => locks.AcquireEntryAsync(file, Token)
        );
    }

    [Fact]
    public async Task AcquireEntryAsync_LinkWhileContentThroughLinkHeld_DoesNotWait()
    {
        var target = Path.Combine(_root, "real.md");
        var link = Path.Combine(_root, "link.md");
        await File.WriteAllTextAsync(target, "content", Token);
        File.CreateSymbolicLink(link, target);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        // 经过链接写入的身份是目标文件，删除或移动链接的身份是链接条目本身，两者互不等待；条目锁本身仍然互斥。
        // A write through the link is identified by the target file and a delete or move of the link by the link entry
        // itself, so neither waits for the other; entry locks themselves remain mutually exclusive.
        await using var content = await locks.AcquireContentAsync(link, Token);
        await using (await locks.AcquireEntryAsync(link, Token).WaitAsync(TimeSpan.FromSeconds(5), Token)) { }
        await AssertWaitsAsync(() => locks.AcquireEntryAsync(link, Token), () => locks.AcquireEntryAsync(link, Token));
    }

    [Theory(SkipUnless = nameof(IsUnix), Skip = "Unix file modes exist on Linux and macOS only.")]
    [InlineData(true)]
    [InlineData(false)]
    [UnsupportedOSPlatform("windows")]
    public async Task AcquireEntryAsync_UnresolvableLink_DoesNotResolveTarget(bool loop)
    {
        var link = Path.Combine(_root, "link.md");
        var locked = UnresolvableLinks.Create(_root, link, loop);
        var locks = new FilePathLocks(new InMemoryApplicationLock());
        try
        {
            await using (await locks.AcquireEntryAsync(link, Token).WaitAsync(TimeSpan.FromSeconds(5), Token)) { }

            await Assert.ThrowsAsync<AgwException>(() => locks.AcquireContentAsync(link, Token));
        }
        finally
        {
            UnresolvableLinks.Restore(locked);
        }
    }

    [Fact]
    public async Task AcquireEntryAsync_TwoPathsOfOneEntry_AcquiresOnce()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var linked = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linked, real);
        var locks = new FilePathLocks(new InMemoryApplicationLock());

        await using (
            await locks
                .AcquireEntryAsync(Path.Combine(linked, "a.md"), Path.Combine(real, "a.md"), Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token)
        ) { }

        await using var reacquired = await locks
            .AcquireEntryAsync(Path.Combine(real, "a.md"), Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task AcquireContentAsync_PhysicalPathKeepsChanging_ThrowsFileOperationFailed()
    {
        var first = Path.Combine(_root, "first.md");
        var second = Path.Combine(_root, "second.md");
        var link = Path.Combine(_root, "link.md");
        await File.WriteAllTextAsync(first, "first", Token);
        await File.WriteAllTextAsync(second, "second", Token);
        File.CreateSymbolicLink(link, first);
        var inner = new InMemoryApplicationLock();
        var pointsToFirst = true;
        // 每次获取之前把链接改为指向另一个文件，使取得锁之后重新解析的物理路径总是不同。
        // Before every acquisition the link is repointed to the other file, so the physical path resolved again after
        // the lock is taken always differs.
        var locks = new FilePathLocks(
            new BeforeAcquireApplicationLock(
                inner,
                () =>
                {
                    pointsToFirst = !pointsToFirst;
                    File.Delete(link);
                    File.CreateSymbolicLink(link, pointsToFirst ? first : second);
                }
            )
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() => locks.AcquireContentAsync(link, Token));

        Assert.Equal(ErrorCodes.FileOperationFailed.Code, exception.Code);
        var check = new FilePathLocks(inner);
        await using (await check.AcquireContentAsync(first, Token).WaitAsync(TimeSpan.FromSeconds(5), Token)) { }
        await using (await check.AcquireContentAsync(second, Token).WaitAsync(TimeSpan.FromSeconds(5), Token)) { }
    }

    /// <summary>
    /// 持有第一个锁时，第二个获取必须等待，释放后才完成。
    /// While the first lock is held, the second acquisition must wait and completes only after the release.
    /// </summary>
    private static async Task AssertWaitsAsync(
        Func<Task<FilePathLease>> acquireFirst,
        Func<Task<FilePathLease>> acquireSecond
    )
    {
        var held = await acquireFirst();
        var waiting = acquireSecond();
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.False(waiting.IsCompleted);
        await held.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    /// <summary>
    /// 每次获取之前执行回调，再交给内部的应用锁获取，用来在获取锁的过程中制造路径变化。
    /// Runs a callback before every acquisition and then delegates to the inner application lock, creating path
    /// changes while a lock is being acquired.
    /// </summary>
    private sealed class BeforeAcquireApplicationLock : IApplicationLock
    {
        private readonly IApplicationLock _inner;
        private readonly Action _beforeAcquire;

        public BeforeAcquireApplicationLock(IApplicationLock inner, Action beforeAcquire)
        {
            _inner = inner;
            _beforeAcquire = beforeAcquire;
        }

        public Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
        {
            _beforeAcquire();
            return _inner.AcquireAsync(resourceName, cancellationToken);
        }
    }
}
