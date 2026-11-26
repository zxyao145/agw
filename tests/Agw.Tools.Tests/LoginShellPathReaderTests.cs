using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ContextualTools.Shell;
using static Agw.Tools.Tests.ZshTestEnvironment;

namespace Agw.Tools.Tests;

public sealed class LoginShellPathReaderTests : IDisposable
{
    private readonly string _directory;

    public LoginShellPathReaderTests()
    {
        _directory = Path.Combine(
            AppContext.BaseDirectory,
            ".scratch",
            "login-shell-path",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void HasLoginShell_ShellMissing_ReturnsFalse()
    {
        var environment = ShellEnvironment("  ");
        Assert.False(LoginShellPathReader.HasLoginShell(environment));

        environment.Remove("SHELL");
        Assert.False(LoginShellPathReader.HasLoginShell(environment));
    }

    [Fact]
    public void HasLoginShell_UnsupportedShell_ReturnsFalse() =>
        Assert.False(LoginShellPathReader.HasLoginShell(ShellEnvironment("/usr/local/bin/fish")));

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/bash")]
    [InlineData("/bin/zsh")]
    [InlineData("/bin/dash")]
    [InlineData("/bin/ksh")]
    public void HasLoginShell_SupportedShell_ReturnsTrue(string shell) =>
        Assert.True(LoginShellPathReader.HasLoginShell(ShellEnvironment(shell)));

    [Fact]
    public async Task ReadAsync_RelativeShellPath_ThrowsShellLoginPathUnavailable()
    {
        var exception = await ReadFailureAsync(ShellEnvironment("zsh"));

        Assert.Equal("Login shell path is not absolute: zsh", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_ShellExecutableMissing_ThrowsShellLoginPathUnavailable()
    {
        var exception = await ReadFailureAsync(ShellEnvironment(Path.Combine(_directory, "missing", "zsh")));

        Assert.StartsWith("Cannot start login shell:", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_StartupConfigurationSetsPath_ReturnsNormalizedEntriesInOrder()
    {
        await WriteZshrcAsync(
            """
            printf 'shell startup\n'
            cd "$ZDOTDIR"
            export HOME="$ZDOTDIR/other-home"
            export PATH="cli tools::./node tools:~/.dotnet/tools:~:~someone/tools:.:/opt/tool/link/../bin:/usr/bin"
            """
        );

        var result = await ReadAsync(ShellEnvironment());

        Assert.Equal(
            string.Join(
                ':',
                "cli tools",
                "./node tools",
                $"{_directory}/.dotnet/tools",
                _directory,
                "~someone/tools",
                ".",
                "/opt/tool/link/../bin",
                "/usr/bin"
            ),
            result
        );
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_BackgroundProcessHoldsStdout_ReturnsPath()
    {
        await WriteZshrcAsync("export PATH=/usr/bin\n/bin/sleep 30 &\n");

        var result = await ReadAsync(ShellEnvironment());

        Assert.Equal("/usr/bin", result);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_EmptyPath_ThrowsShellLoginPathUnavailable()
    {
        await WriteZshrcAsync("export PATH=");

        var exception = await ReadFailureAsync(ShellEnvironment());

        Assert.Equal("Login shell returned an empty search path.", exception.Message);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_PathExceedsLimit_ThrowsShellLoginPathUnavailable()
    {
        await WriteZshrcAsync(
            """
            export PATH="${(l:70000::a:):-}"
            """
        );

        var exception = await ReadFailureAsync(ShellEnvironment());

        Assert.Equal("Login shell search path exceeds 64 KiB.", exception.Message);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_StartupConfigurationTimesOut_ThrowsShellLoginPathUnavailable()
    {
        await WriteZshrcAsync("/bin/sleep 30");

        var exception = await ReadFailureAsync(ShellEnvironment(), TimeSpan.FromMilliseconds(200));

        Assert.Equal("Login shell lookup timed out.", exception.Message);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_ShellExitsWithoutFrame_ThrowsShellLoginPathUnavailable()
    {
        await WriteZshrcAsync("exit 7");

        var exception = await ReadFailureAsync(ShellEnvironment());

        Assert.Equal("Login shell exited with code 7 without returning a valid search path.", exception.Message);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_ShellExitsWhileBackgroundProcessHoldsStdout_ThrowsShellLoginPathUnavailable()
    {
        await WriteZshrcAsync("/bin/sleep 30 &\nexit 7\n");

        var exception = await ReadFailureAsync(ShellEnvironment());

        Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task ReadAsync_CallerCancels_ThrowsOperationCanceledException()
    {
        await WriteZshrcAsync("/bin/sleep 30");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LoginShellPathReader.ReadAsync(ShellEnvironment(), LoginShellPathReader.DefaultTimeout, cancellation.Token)
        );
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Dictionary<string, string> ShellEnvironment(string shell = "/bin/zsh") =>
        new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = _directory,
            ["USER"] = Environment.UserName,
            ["SHELL"] = shell,
            ["ZDOTDIR"] = _directory,
        };

    private Task WriteZshrcAsync(string content) =>
        File.WriteAllTextAsync(
            Path.Combine(_directory, ".zshrc"),
            content.ReplaceLineEndings("\n"),
            TestContext.Current.CancellationToken
        );

    private static Task<string> ReadAsync(Dictionary<string, string> environment, TimeSpan? timeout = null) =>
        LoginShellPathReader.ReadAsync(
            environment,
            timeout ?? LoginShellPathReader.DefaultTimeout,
            TestContext.Current.CancellationToken
        );

    private static async Task<AgwException> ReadFailureAsync(
        Dictionary<string, string> environment,
        TimeSpan? timeout = null
    )
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() => ReadAsync(environment, timeout));
        Assert.Equal(ErrorCodes.ShellLoginPathUnavailable.Code, exception.Code);
        return exception;
    }
}
