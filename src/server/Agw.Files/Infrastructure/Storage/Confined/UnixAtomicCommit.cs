using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// Unix 上原子提交的系统调用，LocalFileSystem 与 UnixConfinedFileSystem 共用：全部以目标所在目录的句柄为基准，临时文件
/// 使用 openat（O_CREAT | O_EXCL | O_NOFOLLOW）创建，复核使用 openat（O_NOFOLLOW）重新打开目标，提交使用 renameat
/// （替换）或不替换的原子重命名，都不按路径重新解析。
/// The Unix system calls of the atomic commit shared by LocalFileSystem and UnixConfinedFileSystem: everything is
/// relative to the handle of the target's directory. The temporary file is created with openat (O_CREAT | O_EXCL |
/// O_NOFOLLOW), the re-check reopens the target with openat (O_NOFOLLOW), and the commit is renameat (replacing) or the
/// atomic no-replace rename, none of which resolves a path again.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixAtomicCommit
{
    /// <summary>
    /// 临时文件创建时的权限（再经过 umask）；目标文件存在时随后复制它的权限位。
    /// The mode of a newly created temporary file (subject to umask); the target's permission bits are copied
    /// afterwards when it exists.
    /// </summary>
    private const uint TemporaryFileMode = 0x1B6;

    /// <summary>
    /// 原子提交：目标已存在而不可写时直接失败；否则在目录句柄下创建临时文件，复制权限位与内容并刷新到磁盘，再复核并重命名为
    /// <paramref name="name"/>；提交前失败或取消时删除临时文件，删除失败只记录警告。复制使用
    /// <see cref="FilePathLease.Token"/>，调用方取消或租约丢失时中止。<paramref name="path"/> 用于错误信息，
    /// <paramref name="logPath"/> 用于日志。
    /// The atomic commit: fails at once when the target exists and is not writable; otherwise creates the temporary
    /// file under the directory handle, copies the permission bits and content and flushes them to disk, then
    /// re-checks and renames it to <paramref name="name"/>; the temporary file is deleted when anything fails or is
    /// canceled before the commit, and a failed delete is only logged as a warning. Copying uses
    /// <see cref="FilePathLease.Token"/> and aborts when the caller cancels or a lease is lost.
    /// <paramref name="path"/> is used in error messages and <paramref name="logPath"/> in logs.
    /// </summary>
    public static async Task WriteAsync(
        SafeFileHandle directory,
        string name,
        Stream content,
        UnixFileMode? permissions,
        bool overwrite,
        FileVersion? expected,
        FilePathLease pathLock,
        string path,
        string logPath,
        ILogger logger
    )
    {
        RequireWritableTarget(directory, name, path);
        var temporaryName = AtomicFileWrite.CreateTemporaryName(name);
        var created = false;
        var committed = false;
        try
        {
            using (var temporary = CreateTemporary(directory, temporaryName, path))
            {
                created = true;
                if (permissions is { } mode)
                {
                    File.SetUnixFileMode(temporary, mode);
                }

                await AtomicFileWrite.CopyAsync(content, temporary, pathLock.Token).ConfigureAwait(false);
            }

            Commit(directory, temporaryName, name, overwrite, expected, pathLock, path);
            committed = true;
        }
        finally
        {
            if (created && !committed)
            {
                AtomicFileWrite.DeleteTemporaryFile(
                    () => DeleteTemporary(directory, temporaryName),
                    logPath,
                    temporaryName,
                    logger
                );
            }
        }
    }

    public static SafeFileHandle OpenDirectory(string directoryPath, string path)
    {
        var fd = UnixNative.Open(directoryPath, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_CLOEXEC);
        return fd >= 0
            ? UnixNative.WrapFd(fd)
            : throw UnixNative.CreateException(UnixNative.LastError, $"Cannot open the directory of '{path}'");
    }

    /// <summary>
    /// 在目录句柄下以 O_NOFOLLOW 打开已有文件用于读取；名称不存在或已被换成符号链接时返回 null。
    /// Opens an existing file under the directory handle with O_NOFOLLOW for reading; returns null when the name is
    /// missing or has been replaced by a symbolic link.
    /// </summary>
    public static SafeFileHandle? OpenExisting(SafeFileHandle directory, string name, string path)
    {
        var fd = UnixNative.OpenAt(
            UnixNative.Fd(directory),
            name,
            UnixNative.O_RDONLY | UnixNative.O_NONBLOCK | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
        );
        if (fd >= 0)
        {
            return UnixNative.WrapFd(fd);
        }

        var errno = UnixNative.LastError;
        return errno == UnixNative.ENOENT || errno == UnixNative.ELOOP
            ? null
            : throw UnixNative.CreateException(errno, $"Cannot open '{path}'");
    }

    /// <summary>
    /// 目标已存在而当前进程的有效用户不能写入它时（权限位、ACL、不可修改标志或只读文件系统）抛出 <c>FileNotWritable</c>，
    /// 只读文件不被替换；目标不存在时不做任何事。
    /// Throws <c>FileNotWritable</c> when the target exists and the effective user of this process may not write it
    /// (mode bits, ACLs, an immutable flag or a read-only file system), so a read-only file is never replaced; does
    /// nothing when the target does not exist.
    /// </summary>
    private static void RequireWritableTarget(SafeFileHandle directory, string name, string path)
    {
        if (UnixNative.FAccessAt(UnixNative.Fd(directory), name, UnixNative.W_OK, UnixNative.AT_EACCESS) == 0)
        {
            return;
        }

        var errno = UnixNative.LastError;
        if (errno == UnixNative.ENOENT)
        {
            return;
        }

        throw errno is UnixNative.EACCES or UnixNative.EPERM or UnixNative.EROFS
            ? AtomicFileWrite.NotWritable(path)
            : UnixNative.CreateException(errno, $"Cannot check write permission of '{path}'");
    }

    /// <summary>
    /// 在目录句柄下以“必须新建”的方式创建临时文件。
    /// Creates the temporary file under the directory handle, failing when the name already exists.
    /// </summary>
    private static SafeFileHandle CreateTemporary(SafeFileHandle directory, string temporaryName, string path)
    {
        var fd = UnixNative.OpenAtCreate(
            UnixNative.Fd(directory),
            temporaryName,
            UnixNative.O_RDWR | UnixNative.O_EXCL | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC,
            TemporaryFileMode
        );
        return fd >= 0
            ? UnixNative.WrapFd(fd)
            : throw UnixNative.CreateException(UnixNative.LastError, $"Cannot create a temporary file for '{path}'");
    }

    /// <summary>
    /// 复核并提交：调用方已取消或租约已丢失时抛出 <see cref="OperationCanceledException"/>；有写入前提时重新打开目标，文件
    /// 标识、大小或修改时间与哈希检查时不同则抛出 <c>FileChanged</c>；随后重命名临时文件。复核与重命名在同一线程中连续执行，
    /// 中间没有 await。不替换时目标已存在由重命名本身失败（EEXIST），抛出 <c>FileAlreadyExists</c>。
    /// Re-checks and commits: throws <see cref="OperationCanceledException"/> when the caller has canceled or the lease
    /// is lost; with a write precondition the target is reopened and <c>FileChanged</c> is thrown when its identity,
    /// size or modification time differs from the hash check; then the temporary file is renamed. The re-check and the
    /// rename run back to back on one thread with no await in between. Without overwrite an existing target makes the
    /// rename itself fail (EEXIST), which throws <c>FileAlreadyExists</c>.
    /// </summary>
    private static void Commit(
        SafeFileHandle directory,
        string temporaryName,
        string name,
        bool overwrite,
        FileVersion? expected,
        FilePathLease pathLock,
        string path
    )
    {
        pathLock.ThrowIfLost();
        if (expected is { } version)
        {
            using var current = OpenExisting(directory, name, path) ?? throw AtomicFileWrite.FileChanged(path);
            AtomicFileWrite.VerifyVersion(version, UnixNative.Stat(current).Version, path);
        }

        var fd = UnixNative.Fd(directory);
        if (UnixNative.RenameAt(fd, temporaryName, fd, name, replace: overwrite) == 0)
        {
            return;
        }

        var errno = UnixNative.LastError;
        throw !overwrite && errno == UnixNative.EEXIST
            ? AtomicFileWrite.AlreadyExists(path)
            : UnixNative.CreateException(errno, $"Cannot replace '{path}'");
    }

    /// <summary>
    /// 删除目录句柄下的临时文件；文件已不存在时不做任何事。
    /// Deletes a temporary file under the directory handle; does nothing when it no longer exists.
    /// </summary>
    public static void DeleteTemporary(SafeFileHandle directory, string temporaryName)
    {
        if (UnixNative.UnlinkAt(UnixNative.Fd(directory), temporaryName, 0) == 0)
        {
            return;
        }

        var errno = UnixNative.LastError;
        if (errno != UnixNative.ENOENT)
        {
            throw UnixNative.CreateException(errno, $"Cannot delete temporary file '{temporaryName}'");
        }
    }
}
