namespace Agw.Files.Tests;

/// <summary>
/// 删除测试目录。Windows 上 git 把对象文件设为只读，Directory.Delete 遇到只读文件会拒绝访问，因此先清除只读属性；符号链接
/// 与 junction 不进入。
/// Deletes a test directory. Git marks object files read-only on Windows, where Directory.Delete refuses read-only
/// files, so the read-only attribute is cleared first; symbolic links and junctions are not entered.
/// </summary>
internal static class TestDirectories
{
    public static void Delete(string path)
    {
        var files = Directory.EnumerateFiles(
            path,
            "*",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }
        );
        foreach (var file in files)
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
