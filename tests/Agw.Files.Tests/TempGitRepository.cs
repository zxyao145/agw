using System.Diagnostics;

namespace Agw.Files.Tests;

/// <summary>
/// 测试用的 git 仓库，位于仓库的 .scratch 目录中，释放时删除。
/// A git repository for tests under the repository .scratch directory and deleted on dispose.
/// </summary>
internal sealed class TempGitRepository : IDisposable
{
    private TempGitRepository(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TempGitRepository Create()
    {
        var path = TestDirectoryHelper.Create("git-repositories");
        return InitializeIn(path);
    }

    /// <summary>在已有目录中初始化仓库；释放时删除这个目录。Initializes a repository in an existing directory, deleted on dispose.</summary>
    public static TempGitRepository InitializeIn(string path)
    {
        var repository = new TempGitRepository(path);
        repository.Git("init", "--quiet");
        repository.Git("config", "user.email", "tests@agw.local");
        repository.Git("config", "user.name", "Agw Tests");
        repository.Git("config", "core.autocrlf", "false");
        repository.Git("config", "commit.gpgsign", "false");
        return repository;
    }

    public string GetPath(string relativePath) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));

    public void Write(string relativePath, string content)
    {
        File.WriteAllText(GetPath(relativePath), content);
    }

    public void WriteBytes(string relativePath, byte[] content)
    {
        File.WriteAllBytes(GetPath(relativePath), content);
    }

    public void Delete(string relativePath)
    {
        File.Delete(GetPath(relativePath));
    }

    public void CommitAll()
    {
        Git("add", ".");
        Git("commit", "--quiet", "-m", "test fixture");
    }

    public void Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {standardError}{standardOutput}"
        );
    }

    public void Dispose()
    {
        TestDirectoryHelper.Delete(Path);
    }
}
