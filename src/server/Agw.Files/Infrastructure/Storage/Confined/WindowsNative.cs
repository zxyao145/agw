using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Agw.Shared.Exceptions;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// Windows 文件系统实现用到的 Win32 调用：打开句柄、读取句柄的最终物理路径与文件信息、按句柄删除与重命名。
/// Win32 calls used by the Windows file system implementations: opening handles, reading a handle's final physical
/// path and file information, and deleting and renaming by handle.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_TRAVERSE = 0x20;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint ShareAll = 0x1 | 0x2 | 0x4;

    /// <summary>
    /// FILE_SHARE_READ | FILE_SHARE_WRITE，不含 FILE_SHARE_DELETE。句柄请求了读取、写入、执行或删除访问（例如目录的
    /// FILE_TRAVERSE）时，持有期间对象不能被重命名或删除；只请求属性访问（FILE_READ_ATTRIBUTES）的句柄不参与共享检查，共享
    /// 方式不起作用。
    /// FILE_SHARE_READ | FILE_SHARE_WRITE without FILE_SHARE_DELETE. When the handle requests read, write, execute or
    /// delete access (for example FILE_TRAVERSE on a directory), the object cannot be renamed or deleted while it is
    /// held; a handle requesting only attribute access (FILE_READ_ATTRIBUTES) takes no part in share checks, so its
    /// share mode has no effect.
    /// </summary>
    public const uint ShareReadWrite = 0x1 | 0x2;
    public const uint CREATE_NEW = 1;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_DIR_NOT_EMPTY = 145;
    public const int ERROR_ALREADY_EXISTS = 183;

    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    /// <summary>
    /// FILE_RENAME_INFO 的定长部分；FileName 是变长 UTF-16 名称的第一个字符，只用于计算它的偏移。
    /// The fixed part of FILE_RENAME_INFO; FileName is the first character of the variable-length UTF-16 name and is
    /// used only to compute its offset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfoHeader
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    /// <summary>
    /// BY_HANDLE_FILE_INFORMATION；每个 FILETIME 分成低位、高位两个 DWORD，布局与 fileapi.h 一致。
    /// BY_HANDLE_FILE_INFORMATION with each FILETIME split into its low and high DWORDs, matching the layout in
    /// fileapi.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information
    );

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

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        byte[] information,
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
    /// 通过 GetFileInformationByHandle 读取句柄所指文件的身份（卷序列号与文件索引）、大小与修改时间。
    /// Reads the identity (volume serial number and file index), size and modification time of the file behind the
    /// handle through GetFileInformationByHandle.
    /// </summary>
    public static FileVersion GetFileVersion(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw CreateException(LastError, "GetFileInformationByHandle failed");
        }

        var fileIndex = ((long)information.FileIndexHigh << 32) | information.FileIndexLow;
        var size = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var lastWriteTime = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        return new FileVersion(
            new FileIdentity(information.VolumeSerialNumber, fileIndex),
            size,
            DateTimeOffset.FromFileTime(lastWriteTime).ToUniversalTime()
        );
    }

    /// <summary>
    /// 通过句柄标记删除；句柄关闭时对象被删除。目录必须为空，非空时抛出 <c>DirectoryNotEmpty</c>。
    /// Marks the object for deletion through its handle; it is removed when the handle closes. A directory must be
    /// empty, otherwise <c>DirectoryNotEmpty</c> is thrown.
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
            var error = LastError;
            throw error == ERROR_DIR_NOT_EMPTY
                ? new AgwException(ErrorCodes.DirectoryNotEmpty, $"{description}: the directory is not empty.")
                : CreateException(error, description);
        }
    }

    /// <summary>
    /// 通过句柄重命名：对象移动到 <paramref name="directory"/> 句柄所指目录下的 <paramref name="name"/>，目标位置相对已打开
    /// 的目录句柄解析，不再经过路径。<paramref name="replace"/> 为 false 时目标已存在会失败（ERROR_ALREADY_EXISTS），返回
    /// Win32 错误码，成功时返回 0。
    /// Renames through a handle: the object moves to <paramref name="name"/> under the directory behind the
    /// <paramref name="directory"/> handle, so the destination is resolved relative to the opened directory handle
    /// instead of a path. When <paramref name="replace"/> is false an existing destination fails
    /// (ERROR_ALREADY_EXISTS). Returns the Win32 error code, or 0 on success.
    /// </summary>
    public static int Rename(SafeFileHandle handle, SafeFileHandle directory, string name, bool replace)
    {
        var rootOffset = (int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.RootDirectory));
        var lengthOffset = (int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.FileNameLength));
        var nameOffset = (int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.FileName));
        var nameBytes = MemoryMarshal.AsBytes(name.AsSpan());
        // 名称之后保留一个 NUL 字符，长度不小于结构体本身。The name keeps a trailing NUL and the size covers the struct.
        var information = new byte[
            Math.Max(Marshal.SizeOf<FileRenameInfoHeader>(), nameOffset + nameBytes.Length + sizeof(char))
        ];
        var addedReference = false;
        try
        {
            directory.DangerousAddRef(ref addedReference);
            information[0] = replace ? (byte)1 : (byte)0;
            MemoryMarshal.Write(information.AsSpan(rootOffset), directory.DangerousGetHandle());
            MemoryMarshal.Write(information.AsSpan(lengthOffset), (uint)nameBytes.Length);
            nameBytes.CopyTo(information.AsSpan(nameOffset));
            return SetFileInformationByHandle(handle, FileRenameInfoClass, information, (uint)information.Length)
                ? 0
                : LastError;
        }
        finally
        {
            if (addedReference)
            {
                directory.DangerousRelease();
            }
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
