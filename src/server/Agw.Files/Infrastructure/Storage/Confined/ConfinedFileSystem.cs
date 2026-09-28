using Agw.Files.Abstracts;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// 创建受限文件系统：Linux 与 macOS 上使用绑定目录句柄的 <see cref="UnixConfinedFileSystem"/>；Windows 上使用在每次操作前
/// 解析符号链接并检查物理路径的 <see cref="WindowsConfinedFileSystem"/>。
/// Creates a confined file system: <see cref="UnixConfinedFileSystem"/>, bound to directory handles, on Linux and
/// macOS; <see cref="WindowsConfinedFileSystem"/>, which resolves symbolic links and checks the physical path before
/// every operation, on Windows.
/// </summary>
internal static class ConfinedFileSystem
{
    public static IAgwFileSystem Create(string rootFullPath, IReadOnlyList<string> allowedRoots)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsConfinedFileSystem.Create(rootFullPath, allowedRoots);
        }

        return new UnixConfinedFileSystem(rootFullPath, [], UnixConfinedFileSystem.ResolveAllowedRoots(allowedRoots));
    }
}
