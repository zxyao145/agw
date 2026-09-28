namespace Agw.Files.Abstracts;

/// <summary>
/// 根目录位于宿主机本地磁盘的文件系统抽象，可以把根目录下的相对路径与宿主机物理路径互相转换。
/// File system rooted at a local host directory; converts between root-relative paths and host physical paths.
/// </summary>
public interface ILocalFileSystem : IAgwFileSystem
{
    string ResolvePhysicalPath(string path);

    string GetRelativePath(string fullPath);

    /// <summary>
    /// 返回同一根目录上的受限文件系统：每个操作都绑定到经过校验的目录或文件句柄，路径经过符号链接后的物理位置必须位于
    /// <paramref name="allowedRoots"/>（物理目录路径）之一内，否则抛出 <c>FilePathOutsideRoot</c>。
    /// Returns a confined file system over the same root: every operation is bound to verified directory or file
    /// handles, and the physical location of a path after symbolic links must stay inside one of
    /// <paramref name="allowedRoots"/> (physical directory paths), otherwise <c>FilePathOutsideRoot</c> is thrown.
    /// </summary>
    IAgwFileSystem Confine(IReadOnlyList<string> allowedRoots);
}
