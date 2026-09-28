using System.Runtime.InteropServices;
using Agw.Shared.Exceptions;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// 受限文件系统用到的 Unix 系统调用：以目录句柄为基准、且不跟随符号链接的 *at 系列调用，加上 .NET 运行时自带的
/// libSystem.Native 中布局稳定的 fstat 与 readdir。所有符号都从当前进程已加载的镜像中解析。
/// Unix system calls used by the confined file system: the directory-handle-relative *at calls that do not follow
/// symbolic links, plus fstat and readdir from the .NET runtime's own libSystem.Native, whose layouts are stable. Every
/// symbol is resolved from the images already loaded in the current process.
/// </summary>
internal static partial class UnixNative
{
    private const string Library = "agw-native";

    public const int O_RDONLY = 0;
    public const int O_RDWR = 2;
    public static readonly int O_CREAT;
    public static readonly int O_EXCL;
    public static readonly int O_NONBLOCK;
    public static readonly int O_CLOEXEC;
    public static readonly int O_DIRECTORY;
    public static readonly int O_NOFOLLOW;
    public static readonly int AT_REMOVEDIR;

    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EEXIST = 17;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public static readonly int ELOOP;

    public const int S_IFMT = 0xF000;
    public const int S_IFDIR = 0x4000;
    public const int S_IFREG = 0x8000;

    public const int DT_DIR = 4;
    public const int DT_REG = 8;
    public const int DT_LNK = 10;

    private static readonly bool UseInode64Symbols;

    static UnixNative()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(UnixNative).Assembly,
            static (name, _, _) => name == Library ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero
        );

        if (OperatingSystem.IsMacOS())
        {
            O_CREAT = 0x200;
            O_EXCL = 0x800;
            O_NONBLOCK = 0x4;
            O_CLOEXEC = 0x1000000;
            O_DIRECTORY = 0x100000;
            O_NOFOLLOW = 0x100;
            AT_REMOVEDIR = 0x80;
            ELOOP = 62;
            UseInode64Symbols = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new AgwException(
                ErrorCodes.FileOperationFailed,
                "The confined file system supports Linux and macOS only."
            );
        }

        O_CREAT = 0x40;
        O_EXCL = 0x80;
        O_NONBLOCK = 0x800;
        O_CLOEXEC = 0x80000;
        AT_REMOVEDIR = 0x200;
        ELOOP = 40;
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
            case Architecture.X86:
                O_DIRECTORY = 0x10000;
                O_NOFOLLOW = 0x20000;
                break;
            case Architecture.Arm64:
                O_DIRECTORY = 0x4000;
                O_NOFOLLOW = 0x8000;
                break;
            default:
                throw new AgwException(
                    ErrorCodes.FileOperationFailed,
                    $"The confined file system does not support the {RuntimeInformation.ProcessArchitecture} architecture on Linux."
                );
        }
    }

    /// <summary>
    /// libSystem.Native 的 FileStatus 结构，与 .NET 运行时 pal_io.h 中的定义一致。
    /// The FileStatus structure of libSystem.Native, matching the definition in the .NET runtime's pal_io.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime;
        public long ATimeNsec;
        public long MTime;
        public long MTimeNsec;
        public long CTime;
        public long CTimeNsec;
        public long BirthTime;
        public long BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;

        public readonly bool IsDirectory => (Mode & S_IFMT) == S_IFDIR;
        public readonly bool IsRegularFile => (Mode & S_IFMT) == S_IFREG;
        public readonly FileIdentity Identity => new(Dev, Ino);
        public readonly DateTimeOffset LastModifiedUtc =>
            DateTimeOffset.FromUnixTimeSeconds(MTime).AddTicks(MTimeNsec / 100);
    }

    /// <summary>
    /// libSystem.Native 的 DirectoryEntry 结构；NameLength 为负表示名称以 NUL 结尾。
    /// The DirectoryEntry structure of libSystem.Native; a negative NameLength means the name is NUL-terminated.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DirectoryEntry
    {
        public IntPtr Name;
        public int NameLength;
        public int InodeType;
    }

    [LibraryImport(Library, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Open(string path, int flags);

    [LibraryImport(Library, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int OpenAt(int directory, string name, int flags);

    [LibraryImport(Library, EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int MkDirAt(int directory, string name, uint mode);

    [LibraryImport(Library, EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int UnlinkAt(int directory, string name, int flags);

    /// <summary>
    /// 带 O_CREAT 的 openat。openat 在 C 中是可变参数函数，mode 属于可变部分：Apple arm64 把可变参数放在栈上，x86-64 与
    /// Linux arm64 则像固定参数一样放在寄存器里。这里把 mode 重复填入第 4 到第 9 个参数，使寄存器 x3 和栈顶第一个槽位都是
    /// mode，无论被调函数从哪里读取都得到正确的值。多余的参数由调用方清理，对被调函数没有影响。
    /// openat with O_CREAT. openat is variadic in C and mode belongs to the variadic part: Apple arm64 passes variadic
    /// arguments on the stack, while x86-64 and Linux arm64 pass them in registers like fixed ones. The mode is repeated
    /// in arguments 4 through 9 so register x3 and the first stack slot both hold it, and the callee reads the right
    /// value wherever it looks. The extra arguments are cleaned up by the caller and do not affect the callee.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtVariadic(
        int directory,
        string name,
        int flags,
        nint mode4,
        nint mode5,
        nint mode6,
        nint mode7,
        nint mode8,
        nint mode9
    );

    public static int OpenAtCreate(int directory, string name, int flags, uint mode)
    {
        var value = (nint)mode;
        return OpenAtVariadic(directory, name, flags | O_CREAT, value, value, value, value, value, value);
    }

    [LibraryImport(Library, EntryPoint = "readlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint ReadLinkAt(int directory, string name, [Out] byte[] buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "fdopendir", SetLastError = true)]
    private static partial IntPtr FdOpenDirDefault(int fd);

    [LibraryImport(Library, EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static partial IntPtr FdOpenDirInode64(int fd);

    public static IntPtr FdOpenDir(int fd) => UseInode64Symbols ? FdOpenDirInode64(fd) : FdOpenDirDefault(fd);

    [LibraryImport(Library, EntryPoint = "SystemNative_FStat", SetLastError = true)]
    public static partial int FStat(nint fd, out FileStatus status);

    [LibraryImport(Library, EntryPoint = "SystemNative_ReadDir")]
    public static partial int ReadDir(IntPtr directory, out DirectoryEntry entry);

    [LibraryImport(Library, EntryPoint = "SystemNative_CloseDir")]
    public static partial int CloseDir(IntPtr directory);

    public static int Fd(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    public static SafeFileHandle WrapFd(int fd) => new((IntPtr)fd, ownsHandle: true);

    public static int LastError => Marshal.GetLastPInvokeError();

    /// <summary>
    /// 把系统调用的 errno 转成带描述的 <see cref="AgwException"/>。
    /// Converts a system call errno into an <see cref="AgwException"/> with a description.
    /// </summary>
    public static AgwException CreateException(int errno, string description) =>
        new(ErrorCodes.FileOperationFailed, $"{description}: {Marshal.GetPInvokeErrorMessage(errno)}");

    public static FileStatus Stat(SafeFileHandle handle)
    {
        if (FStat(Fd(handle), out var status) != 0)
        {
            throw CreateException(LastError, "fstat failed");
        }

        return status;
    }

    /// <summary>
    /// 读取目录句柄下一个名称的符号链接目标；名称不是符号链接或不存在时返回 null。
    /// Reads the symbolic link target of a name under the directory handle; returns null when the name is not a symbolic
    /// link or does not exist.
    /// </summary>
    public static string? ReadLink(SafeFileHandle directory, string name)
    {
        var buffer = new byte[4096];
        var length = ReadLinkAt(Fd(directory), name, buffer, (nuint)buffer.Length);
        if (length < 0)
        {
            var errno = LastError;
            return errno is EINVAL or ENOENT or ENOTDIR ? null : throw CreateException(errno, "readlinkat failed");
        }

        if (length >= buffer.Length)
        {
            throw CreateException(EINVAL, "readlinkat failed: link target too long");
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
    }
}

/// <summary>
/// 文件系统对象的身份：设备号与 inode 号，用于判断目录句柄是否就是某个允许的根目录，不受路径写法与大小写影响。
/// The identity of a file system object: device and inode numbers, used to decide whether a directory handle is one of
/// the allowed roots regardless of path spelling or letter case.
/// </summary>
internal readonly record struct FileIdentity(long Dev, long Ino);
