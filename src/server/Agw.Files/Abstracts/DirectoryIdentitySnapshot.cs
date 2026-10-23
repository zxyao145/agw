using Agw.Files.Infrastructure.Storage.Confined;
using Agw.Shared.Exceptions;

namespace Agw.Files.Abstracts;

/// <summary>
/// 一组目录在捕获时刻的物理身份（Unix 为 device 与 inode，Windows 为卷序列号与文件索引）。路径上的目录被移走、删除后重新创建或被
/// 替换时身份改变，绑定旧目录的缓存文件系统、进程工作目录或挂载据此判断是否需要刷新。
/// The physical identities of a set of directories at capture time (device and inode on Unix, volume serial number and
/// file index on Windows). The identity changes when a directory at a path is moved away, deleted and recreated, or
/// replaced; cached file systems, process working directories, or mounts bound to the old directory use this to decide
/// whether they must be refreshed.
/// </summary>
public sealed class DirectoryIdentitySnapshot
{
    private readonly string[] _paths;
    private readonly FileIdentity[] _identities;

    private DirectoryIdentitySnapshot(string[] paths, FileIdentity[] identities)
    {
        _paths = paths;
        _identities = identities;
    }

    /// <summary>
    /// 读取每个目录当前的身份；任一目录不存在时抛出 <c>ResourceNotFound</c>。
    /// Reads the current identity of every directory; throws <c>ResourceNotFound</c> when any directory is missing.
    /// </summary>
    public static DirectoryIdentitySnapshot Capture(IEnumerable<string> directoryPaths)
    {
        var paths = directoryPaths.ToArray();
        var identities = paths
            .Select(path =>
                DirectoryIdentities.TryRead(path)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound, $"Project directory is unavailable: '{path}'.")
            )
            .ToArray();
        return new DirectoryIdentitySnapshot(paths, identities);
    }

    /// <summary>
    /// 每个路径上的目录仍是捕获时的同一物理目录时返回 true；任一目录缺失或身份改变时返回 false。
    /// Returns true when every path still holds the same physical directory as at capture time; returns false when any
    /// directory is missing or its identity changed.
    /// </summary>
    public bool IsCurrent()
    {
        for (var index = 0; index < _paths.Length; index++)
        {
            if (DirectoryIdentities.TryRead(_paths[index]) != _identities[index])
            {
                return false;
            }
        }

        return true;
    }
}
