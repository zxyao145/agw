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

    public static bool HasZsh => !OperatingSystem.IsWindows() && File.Exists("/bin/zsh");

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
        Assert.Null(result.SearchPath);
    }

    [Fact]
    public async Task FindAsync_MultiplePathEntries_ReturnsFirstExecutable()
    {
        // 准备测试数据。Arrange.
        var firstDirectory = Path.Combine(_directory, "first");
        var secondDirectory = Path.Combine(_directory, "second");
        var executable = CreatePiExecutable(firstDirectory);
        CreatePiExecutable(secondDirectory);
        var searchPath = string.Join(Path.PathSeparator, "", firstDirectory, secondDirectory);

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", Options(searchPath), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal(executable, result.Path, ignoreCase: IsWindows);
    }

    [Fact]
    public async Task FindAsync_RelativePathEntry_ReturnsAbsolutePath()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(_directory);
        var searchPath = Path.GetRelativePath(Environment.CurrentDirectory, _directory);

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", Options(searchPath), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal(executable, result.Path, ignoreCase: IsWindows);
        Assert.True(Path.IsPathFullyQualified(result.Path!));
    }

    [Fact]
    public async Task FindAsync_ExplicitPath_ReturnsExplicitExecutable()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(_directory);
        var otherDirectory = Path.Combine(_directory, "other");
        CreatePiExecutable(otherDirectory);

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync(
            executable,
            Options(otherDirectory),
            TestContext.Current.CancellationToken
        );

        // 验证查找结果。Assert.
        Assert.Equal(executable, result.Path, ignoreCase: IsWindows);
        Assert.Equal(CommandSource.ExplicitPath, result.Source);
        Assert.Null(result.SearchPath);
    }

    [Fact]
    public async Task FindAsync_MissingExplicitPath_ReturnsNotInstalled()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(_directory);
        var missingPath = Path.Combine(_directory, "missing", Path.GetFileName(executable));

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync(
            missingPath,
            Options(_directory),
            TestContext.Current.CancellationToken
        );

        // 验证查找结果。Assert.
        Assert.False(result.IsInstalled);
        Assert.Null(result.Path);
        Assert.Null(result.Source);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FindAsync_EmptyEnvironment_ReturnsNotInstalled()
    {
        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync(
            "pi",
            new CommandLookupOptions { Environment = new Dictionary<string, string>() },
            TestContext.Current.CancellationToken
        );

        // 验证查找结果。Assert.
        Assert.False(result.IsInstalled);
        Assert.Null(result.Path);
        Assert.Null(result.Source);
        Assert.Null(result.Error);
    }

    [Fact(Skip = "此测试需要 Unix。This test requires Unix.", SkipWhen = nameof(IsWindows))]
    public async Task FindAsync_NonExecutableUnixFile_ReturnsNotInstalled()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(_directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", Options(_directory), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.False(result.IsInstalled);
        Assert.Null(result.Path);
    }

    [Fact(Skip = "此测试需要 Unix。This test requires Unix.", SkipWhen = nameof(IsWindows))]
    public async Task FindAsync_SymbolicLink_PreservesEntrypointPath()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(Path.Combine(_directory, "target"));
        var link = Path.Combine(_directory, "pi");
        File.CreateSymbolicLink(link, executable);

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", Options(_directory), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal(link, result.Path);
        Assert.Equal(CommandSource.Path, result.Source);
    }

    [Fact(Skip = "此测试需要 /bin/zsh。This test requires /bin/zsh.", SkipUnless = nameof(HasZsh))]
    public async Task FindAsync_LoginShellPath_ReturnsExecutableFromShellWorkingDirectory()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(Path.Combine(_directory, "tools"));
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            "printf 'shell startup\\n'\ncd \"$HOME\"\nexport PATH=tools\n",
            TestContext.Current.CancellationToken
        );

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", ShellOptions(), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.True(result.IsInstalled, result.Error);
        Assert.Equal(executable, result.Path);
        Assert.Equal(CommandSource.LoginShell, result.Source);
        Assert.Null(result.Error);
        Assert.Equal(Path.GetDirectoryName(executable), result.SearchPath);
    }

    [Fact(Skip = "此测试需要 /bin/zsh。This test requires /bin/zsh.", SkipUnless = nameof(HasZsh))]
    public async Task FindAsync_LoginShellPath_PreservesDirectoryOrderAndResolvesRelativeEntries()
    {
        // 准备测试数据。Arrange.
        var executable = CreatePiExecutable(Path.Combine(_directory, "cli tools"));
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            "cd \"$HOME\"\nexport PATH=\"cli tools::./node tools:.:/usr/bin\"\n",
            TestContext.Current.CancellationToken
        );

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", ShellOptions(), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.Equal(executable, result.Path);
        Assert.Equal(
            string.Join(
                Path.PathSeparator,
                Path.Combine(_directory, "cli tools"),
                Path.Combine(_directory, "node tools"),
                _directory,
                "/usr/bin"
            ),
            result.SearchPath
        );
    }

    [Fact(Skip = "此测试需要 /bin/zsh。This test requires /bin/zsh.", SkipUnless = nameof(HasZsh))]
    public async Task FindAsync_LoginShellCommandMissing_ReturnsNoSearchPath()
    {
        // 准备测试数据。Arrange.
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            "export PATH=\"$HOME/missing\"\n",
            TestContext.Current.CancellationToken
        );

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync("pi", ShellOptions(), TestContext.Current.CancellationToken);

        // 验证查找结果。Assert.
        Assert.False(result.IsInstalled);
        Assert.Null(result.SearchPath);
        Assert.Null(result.Error);
    }

    [Fact(Skip = "此测试需要 /bin/zsh。This test requires /bin/zsh.", SkipUnless = nameof(HasZsh))]
    public async Task FindAsync_LoginShellTimeout_ReturnsDiagnostic()
    {
        // 准备测试数据。Arrange.
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            "/bin/sleep 30\n",
            TestContext.Current.CancellationToken
        );

        // 执行命令查找。Act.
        var result = await CommandUtil.FindAsync(
            "pi",
            ShellOptions() with
            {
                ShellTimeout = TimeSpan.FromMilliseconds(200),
            },
            TestContext.Current.CancellationToken
        );

        // 验证查找结果。Assert.
        Assert.False(result.IsInstalled);
        Assert.Equal("Login shell lookup timed out.", result.Error);
    }

    [Fact(Skip = "此测试需要 /bin/zsh。This test requires /bin/zsh.", SkipUnless = nameof(HasZsh))]
    public async Task FindAsync_LoginShellCanceled_ThrowsOperationCanceledException()
    {
        // 准备测试数据。Arrange.
        await File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            "/bin/sleep 30\n",
            TestContext.Current.CancellationToken
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        // 执行命令查找并验证取消。Act and assert.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandUtil.FindAsync("pi", ShellOptions(), cancellation.Token)
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("pi\0")]
    public async Task FindAsync_InvalidCommand_ThrowsArgumentException(string command)
    {
        // 执行命令查找并验证参数错误。Act and assert.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CommandUtil.FindAsync(command, Options(_directory), TestContext.Current.CancellationToken)
        );
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

    private static CommandLookupOptions Options(string searchPath) =>
        new()
        {
            UseLoginShell = false,
            Environment = new Dictionary<string, string> { ["PATH"] = searchPath },
        };

    private CommandLookupOptions ShellOptions() =>
        new()
        {
            Environment = new Dictionary<string, string>
            {
                ["PATH"] = "",
                ["SHELL"] = "/bin/zsh",
                ["HOME"] = _directory,
                ["ZDOTDIR"] = _directory,
            },
        };

    private static string CreatePiExecutable(string directory)
    {
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, IsWindows ? "pi.exe" : "pi");
        // 使用当前测试进程的可执行文件作为查找目标。
        // Use the current test process executable as the lookup target.
        File.Copy(Environment.ProcessPath!, executable);
        return executable;
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
