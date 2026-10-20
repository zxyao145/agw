using System.Runtime.Versioning;

namespace Agw.Files.Tests;

/// <summary>
/// 创建目标无法解析的符号链接：指向自身的循环链接（ELOOP），或指向没有搜索权限的目录中文件的链接（EACCES）。
/// Creates symbolic links whose target cannot be resolved: a link to itself (ELOOP), or a link to a file inside a
/// directory without search permission (EACCES).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnresolvableLinks
{
    private const UnixFileMode LockedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UnlockedMode = LockedMode | UnixFileMode.UserExecute;

    /// <summary>
    /// 在 <paramref name="link"/> 创建链接；<paramref name="loop"/> 为 false 时在 <paramref name="root"/> 下创建去掉搜索权限
    /// 的 locked 目录并返回它，测试结束前必须交给 <see cref="Restore"/>；循环链接返回 null。
    /// Creates the link at <paramref name="link"/>; when <paramref name="loop"/> is false a locked directory without
    /// search permission is created under <paramref name="root"/> and returned, and it must be passed to
    /// <see cref="Restore"/> before the test ends; a loop link returns null.
    /// </summary>
    public static string? Create(string root, string link, bool loop)
    {
        if (loop)
        {
            File.CreateSymbolicLink(link, link);
            return null;
        }

        var locked = Directory.CreateDirectory(Path.Combine(root, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "secret.md"), "secret");
        File.CreateSymbolicLink(link, Path.Combine(locked, "secret.md"));
        File.SetUnixFileMode(locked, LockedMode);
        return locked;
    }

    /// <summary>
    /// 恢复 locked 目录的搜索权限，使目录可以被检查与删除。
    /// Restores search permission of the locked directory so it can be inspected and deleted.
    /// </summary>
    public static void Restore(string? locked)
    {
        if (locked != null)
        {
            File.SetUnixFileMode(locked, UnlockedMode);
        }
    }
}
