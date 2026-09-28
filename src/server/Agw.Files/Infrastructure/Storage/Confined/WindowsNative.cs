using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Agw.Shared.Exceptions;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// Windows 受限文件系统用到的 Win32 调用：打开句柄、读取句柄的最终物理路径、按句柄删除。
/// Win32 calls used by the Windows confined file system: opening handles, reading a handle's final physical path, and
/// deleting by handle.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint ShareAll = 0x1 | 0x2 | 0x4;
    public const uint CREATE_NEW = 1;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_ALREADY_EXISTS = 183;

    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags
    );

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        ref FileDispositionInfo information,
        uint bufferSize
    );

    public static int LastError => Marshal.GetLastPInvokeError();

    public static AgwException CreateException(int error, string description) =>
        new(ErrorCodes.FileOperationFailed, $"{description}: {Marshal.GetPInvokeErrorMessage(error)}");

    /// <summary>
    /// 返回句柄所指对象的最终物理路径（符号链接与 junction 已解析），去掉 <c>\\?\</c> 前缀。
    /// Returns the final physical path of the object behind the handle (symbolic links and junctions resolved), with
    /// the <c>\\?\</c> prefix removed.
    /// </summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                throw CreateException(LastError, "GetFinalPathNameByHandle failed");
            }

            if (length < buffer.Length)
            {
                return StripExtendedPrefix(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>
    /// 通过句柄标记删除；句柄关闭时对象被删除。目录必须为空。
    /// Marks the object for deletion through its handle; it is removed when the handle closes. A directory must be empty.
    /// </summary>
    public static void MarkForDeletion(SafeFileHandle handle, string description)
    {
        var information = new FileDispositionInfo { DeleteFile = 1 };
        if (
            !SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref information,
                (uint)Marshal.SizeOf<FileDispositionInfo>()
            )
        )
        {
            throw CreateException(LastError, description);
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
