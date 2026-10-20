using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class CommandUtilTests : IDisposable
{
    private readonly string _directory;

    public CommandUtilTests()
    {
        _directory = Path.Combine(AppContext.BaseDirectory, "command-util-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task FindAsync_GitCommandOnPath_ReturnsExecutable()
    {
        // 准备测试数据。Arrange.
        var executable = CreateExecutable(OperatingSystem.IsWindows() ? "git.exe" : "git");
        var options = new CommandLookupOptions
        {
            UseLoginShell = false,
            Environment = new Dictionary<string, string> { ["PATH"] = _directory },
        };

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("git", options, TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal("git", result.Command);
        Assert.True(result.IsInstalled);
        Assert.Equal(executable, result.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(CommandSource.Path, result.Source);
        Assert.Null(result.Error);
    }

    [Theory(Skip = "此测试需要 Windows。This test requires Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData(null, true)]
    [InlineData(".exe", true)]
    [InlineData(".CMD", false)]
    public async Task FindAsync_GitCommandOnWindows_ResolvesAccordingToPathExt(string? pathExt, bool expectedInstalled)
    {
        // 准备测试数据。Arrange.
        var executable = CreateExecutable("git.exe");
        var environment = new Dictionary<string, string> { ["PATH"] = _directory };
        if (pathExt is not null)
        {
            environment["PATHEXT"] = pathExt;
        }
        var options = new CommandLookupOptions { UseLoginShell = false, Environment = environment };

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("git", options, TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal("git", result.Command);
        Assert.Equal(expectedInstalled, result.IsInstalled);
        Assert.Equal(expectedInstalled ? executable : null, result.Path, ignoreCase: true);
        Assert.Equal(expectedInstalled ? CommandSource.Path : (CommandSource?)null, result.Source);
        Assert.Null(result.Error);
    }

    [Fact(Skip = "此测试需要 Unix。This test requires Unix.", SkipWhen = nameof(IsWindows))]
    public async Task FindAsync_GitCommandWithOnlyGitExeOnUnix_ReturnsNotInstalled()
    {
        // 准备测试数据。Arrange.
        CreateExecutable("git.exe");
        var options = new CommandLookupOptions
        {
            UseLoginShell = false,
            Environment = new Dictionary<string, string> { ["PATH"] = _directory, ["PATHEXT"] = ".EXE" },
        };

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("git", options, TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal("git", result.Command);
        Assert.False(result.IsInstalled);
        Assert.Null(result.Path);
        Assert.Null(result.Source);
        Assert.Null(result.Error);
    }

    private string CreateExecutable(string name)
    {
        var executable = Path.Combine(_directory, name);
        // 使用当前测试进程的可执行文件作为查找目标。
        // Use the current test process executable as the lookup target.
        File.Copy(Environment.ProcessPath!, executable);
        return executable;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
