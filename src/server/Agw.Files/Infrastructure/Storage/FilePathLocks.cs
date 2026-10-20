using System.Security.Cryptography;
using System.Text;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;

namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 按文件身份串行的写锁。文件身份按操作改变的对象分为两种（见 <see cref="PhysicalPaths"/>）：
/// <list type="bullet">
/// <item>内容操作（写入、Agent 文件工具的文字写入与替换）：写入实际替换的文件的物理路径，路径每一段中的符号链接都被解析。
/// </item>
/// <item>目录条目操作（删除、移动的源与目标、创建目录）：条目本身的物理路径，父目录按物理路径解析，最后一段不跟随符号
/// 链接，不需要解析链接的目标。</item>
/// </list>
/// 已存在文件的内容身份使用磁盘上的写法，条目身份的最后一段使用请求中的写法。两种写法一致时（Windows 与 macOS 上只有大小写
/// 不同也算一致，资源名已转为小写），普通文件的两种身份相同，对同一个文件的写入与删除、移动互斥；写法不一致时（例如 macOS
/// 上 Unicode 规范化形式不同），两种身份不同，这些操作互不等待。删除或移动符号链接本身改变的是链接条目，不与经过这个链接
/// 写入目标文件的操作互斥。资源名为 <c>files:path:</c> 加身份（Windows 与 macOS 上转为小写）的 SHA-256，通过
/// <see cref="IApplicationLock"/> 取得，同一部署中所有 Host、所有文件系统实例共用。取得锁之后重新解析一次路径，物理路径在
/// 等待期间发生变化时释放并按新的物理路径重试，连续变化超过 <see cref="MaxAcquireAttempts"/> 次时失败。
/// Write locks serialized per file identity. The identity depends on what the operation changes (see
/// <see cref="PhysicalPaths"/>):
/// <list type="bullet">
/// <item>Content operations (writes and the Agent file tool's text writes and replacements): the physical path of the
/// file actually replaced, with symbolic links in every segment resolved.</item>
/// <item>Directory-entry operations (deletes, the source and destination of moves, and directory creation): the
/// physical path of the entry itself, with the parent directory resolved physically and the last segment not
/// followed, so no link target is resolved.</item>
/// </list>
/// For an existing file the content identity uses the on-disk spelling, while the last segment of the entry identity
/// uses the requested spelling. When the two spellings match (on Windows and macOS a difference in letter case alone
/// still matches, because resource names are lowercased), both identities of a regular file are equal, so writes and
/// deletes or moves of one file are mutually exclusive; when they differ (for example in Unicode normalization form on
/// macOS), the identities differ and these operations do not wait for each other. Deleting or moving a symbolic link
/// itself changes the link entry and does not exclude writes through that link to its target. The resource name is
/// <c>files:path:</c> followed by the SHA-256 of the identity (lowercased on Windows and macOS), acquired through
/// <see cref="IApplicationLock"/> and shared by every Host of a deployment and every file system instance. The path is
/// resolved again after the lock is acquired; when the physical path changed while waiting, the lock is released and
/// acquisition retries with the new physical path, failing after
/// <see cref="MaxAcquireAttempts"/> consecutive changes.
/// </summary>
public sealed class FilePathLocks
{
    private const string ResourcePrefix = "files:path:";
    private const int MaxAcquireAttempts = 5;

    private static readonly bool CaseInsensitivePaths = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private readonly IApplicationLock _applicationLock;

    public FilePathLocks(IApplicationLock applicationLock)
    {
        _applicationLock = applicationLock;
    }

    /// <summary>
    /// 取得内容操作的写锁：身份为写入实际替换的文件。
    /// Acquires the write lock of a content operation: the identity is the file a write actually replaces.
    /// </summary>
    public Task<FilePathLease> AcquireContentAsync(string fullPath, CancellationToken ct) =>
        AcquireAsync([fullPath], PhysicalPaths.ResolveContent, ct);

    /// <summary>
    /// 取得目录条目操作的写锁：身份为条目本身，最后一段不跟随符号链接。
    /// Acquires the write lock of a directory-entry operation: the identity is the entry itself, without following the
    /// last segment.
    /// </summary>
    public Task<FilePathLease> AcquireEntryAsync(string fullPath, CancellationToken ct) =>
        AcquireAsync([fullPath], PhysicalPaths.ResolveEntry, ct);

    /// <summary>
    /// 同时取得两个目录条目的写锁（移动的源与目标）：按资源名排序后依次获取，两个条目身份相同时只获取一次，因此方向相反的
    /// 两个并发移动不会彼此无限等待。
    /// Acquires the write locks of two directory entries at once (the source and destination of a move): they are taken
    /// in resource-name order, and only once when both entries have the same identity, so two concurrent moves in
    /// opposite directions can never wait on each other forever.
    /// </summary>
    public Task<FilePathLease> AcquireEntryAsync(string firstFullPath, string secondFullPath, CancellationToken ct) =>
        AcquireAsync([firstFullPath, secondFullPath], PhysicalPaths.ResolveEntry, ct);

    private async Task<FilePathLease> AcquireAsync(
        string[] fullPaths,
        Func<string, string> resolveIdentity,
        CancellationToken ct
    )
    {
        var resources = ResourceNames(fullPaths, resolveIdentity);
        for (var attempt = 0; attempt < MaxAcquireAttempts; attempt++)
        {
            var lease = await AcquireResourcesAsync(resources, ct).ConfigureAwait(false);
            string[] current;
            try
            {
                current = ResourceNames(fullPaths, resolveIdentity);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (current.SequenceEqual(resources, StringComparer.Ordinal))
            {
                return lease;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            resources = current;
        }

        throw new AgwException(
            ErrorCodes.FileOperationFailed,
            $"The physical location of '{string.Join("', '", fullPaths)}' kept changing while its lock was acquired."
        );
    }

    /// <summary>
    /// 按资源名顺序依次获取；中途失败或取消时释放已经取得的租约。
    /// Acquires in resource-name order; leases already taken are released when a later acquisition fails or is
    /// canceled.
    /// </summary>
    private async Task<FilePathLease> AcquireResourcesAsync(string[] resources, CancellationToken ct)
    {
        var leases = new List<IApplicationLockLease>(resources.Length);
        try
        {
            foreach (var resource in resources)
            {
                leases.Add(await _applicationLock.AcquireAsync(resource, ct).ConfigureAwait(false));
            }
        }
        catch
        {
            await new FilePathLease(leases.ToArray(), ct).DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new FilePathLease(leases.ToArray(), ct);
    }

    /// <summary>
    /// 各路径文件身份的资源名，去重后按字符编码顺序排列。
    /// The resource names of the paths' file identities, deduplicated and sorted ordinally.
    /// </summary>
    private static string[] ResourceNames(string[] fullPaths, Func<string, string> resolveIdentity) =>
        fullPaths
            .Select(fullPath => ResourceName(resolveIdentity(fullPath)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ResourceName(string physicalPath)
    {
        var identity = CaseInsensitivePaths ? physicalPath.ToLowerInvariant() : physicalPath;
        return ResourcePrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
