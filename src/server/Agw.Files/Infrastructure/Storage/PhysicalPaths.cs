using System.Runtime.Versioning;
using Agw.Files.Infrastructure.Storage.Confined;
using Agw.Shared.Exceptions;

namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 计算文件身份的两种物理路径：
/// <list type="bullet">
/// <item>内容身份（<see cref="ResolveContent"/>）：解析路径每一段中的符号链接，最后一段指向不存在目标的链接同样解析到它的
/// 目标，即写入实际替换的文件。</item>
/// <item>条目身份（<see cref="ResolveEntry"/>）：父目录按物理路径解析，最后一段不跟随符号链接，即删除、移动实际改变的目录
/// 条目；不需要解析链接的目标。</item>
/// </list>
/// 已存在的部分由系统解析（Unix 上为 realpath，Windows 上为句柄的最终路径）；不存在的部分沿父目录向上找到已存在的祖先目录后
/// 拼接。
/// Computes the two physical paths used as file identities:
/// <list type="bullet">
/// <item>The content identity (<see cref="ResolveContent"/>): symbolic links in every segment are resolved, including
/// a last segment that is a link to a missing target, giving the file a write actually replaces.</item>
/// <item>The entry identity (<see cref="ResolveEntry"/>): the parent directory is resolved physically and the last
/// segment is not followed, giving the directory entry a delete or move actually changes; no link target is
/// resolved.</item>
/// </list>
/// The existing part is resolved by the system (realpath on Unix, the final path of a handle on Windows); the missing
/// part is appended after walking up to the nearest existing ancestor directory.
/// </summary>
internal static class PhysicalPaths
{
    private const int MaxSymbolicLinkHops = 40;

    public static string ResolveContent(string fullPath) => Resolve(fullPath, 0);

    public static string ResolveEntry(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        return parent == null ? Resolve(fullPath, 0) : Path.Combine(Resolve(parent, 0), Path.GetFileName(fullPath));
    }

    private static string Resolve(string fullPath, int hops)
    {
        var existing = OperatingSystem.IsWindows() ? ResolveExistingWindows(fullPath) : UnixNative.RealPath(fullPath);
        if (existing != null)
        {
            return existing;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (parent == null)
        {
            return fullPath;
        }

        var entry = Path.Combine(Resolve(parent, hops), Path.GetFileName(fullPath));
        var linkTarget = OperatingSystem.IsWindows() ? ReadLinkWindows(entry) : UnixNative.ReadLink(entry);
        if (linkTarget == null)
        {
            return Path.GetFullPath(entry);
        }

        if (hops >= MaxSymbolicLinkHops)
        {
            throw new AgwException(
                ErrorCodes.FileOperationFailed,
                $"'{fullPath}' follows more than {MaxSymbolicLinkHops} symbolic links."
            );
        }

        return Resolve(Path.Combine(Path.GetDirectoryName(entry)!, linkTarget), hops + 1);
    }

    /// <summary>
    /// 打开已存在的路径（跟随符号链接与 junction）并返回句柄的最终路径；路径不存在时返回 null。
    /// Opens an existing path (following symbolic links and junctions) and returns the final path of the handle;
    /// returns null when the path does not exist.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ResolveExistingWindows(string fullPath)
    {
        using var handle = WindowsNative.CreateFile(
            fullPath,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.ShareAll,
            IntPtr.Zero,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero
        );
        if (!handle.IsInvalid)
        {
            return WindowsNative.GetFinalPath(handle);
        }

        var error = WindowsNative.LastError;
        return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? null
            : throw WindowsNative.CreateException(error, $"Cannot open '{fullPath}'");
    }

    /// <summary>
    /// 读取最后一段的链接目标：最后一段存在且是重解析点（符号链接或 junction）时返回目标，否则返回 null。
    /// Reads the link target of the last segment: returns it when the last segment exists and is a reparse point (a
    /// symbolic link or junction), otherwise null.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadLinkWindows(string fullPath)
    {
        using var handle = WindowsNative.CreateFile(
            fullPath,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.ShareAll,
            IntPtr.Zero,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            var error = WindowsNative.LastError;
            return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
                ? null
                : throw WindowsNative.CreateException(error, $"Cannot open '{fullPath}'");
        }

        return (File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0
            ? new FileInfo(fullPath).LinkTarget
            : null;
    }
}
