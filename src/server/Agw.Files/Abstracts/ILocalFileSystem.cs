namespace Agw.Files.Abstracts;

/// <summary>
/// 根目录位于宿主机本地磁盘的文件系统抽象，可以把根目录下的相对路径与宿主机物理路径互相转换。
/// File system rooted at a local host directory; converts between root-relative paths and host physical paths.
/// </summary>
public interface ILocalFileSystem : IAgwFileSystem
{
    string ResolvePhysicalPath(string path);

    string GetRelativePath(string fullPath);
}
