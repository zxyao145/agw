using System.Runtime.Versioning;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// 读取目录的物理身份：Unix 上为 fstat 返回的 device 与 inode，Windows 上为 GetFileInformationByHandle 返回的卷序列号与文件索引。
/// 目录被移走、删除后重新创建或被替换时，同一路径对应的身份随之改变。
/// Reads the physical identity of a directory: the device and inode from fstat on Unix, and the volume serial number
/// and file index from GetFileInformationByHandle on Windows. The identity behind a path changes when the directory is
/// moved away, deleted and recreated, or replaced.
/// </summary>
internal static class DirectoryIdentities
{
    /// <summary>
    /// 读取路径上目录当前的身份；路径不存在或不是目录时返回 null。
    /// Reads the current identity of the directory at the path; returns null when the path is missing or is not a
    /// directory.
    /// </summary>
    public static FileIdentity? TryRead(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindows(directoryPath);
        }

        var fd = UnixNative.Open(directoryPath, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_CLOEXEC);
        if (fd < 0)
        {
            var errno = UnixNative.LastError;
            return errno is UnixNative.ENOENT or UnixNative.ENOTDIR
                ? null
                : throw UnixNative.CreateException(errno, $"Cannot open Project directory '{directoryPath}'");
        }

        using var handle = UnixNative.WrapFd(fd);
        return UnixNative.Stat(handle).Identity;
    }

    [SupportedOSPlatform("windows")]
    private static FileIdentity? TryReadWindows(string directoryPath)
    {
        using var handle = WindowsNative.CreateFile(
            directoryPath,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.ShareAll,
            IntPtr.Zero,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            var error = WindowsNative.LastError;
            return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
                ? null
                : throw WindowsNative.CreateException(error, $"Cannot open Project directory '{directoryPath}'");
        }

        return (File.GetAttributes(handle) & FileAttributes.Directory) == 0
            ? null
            : WindowsNative.GetFileVersion(handle).Identity;
    }
}
