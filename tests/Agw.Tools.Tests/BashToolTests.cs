using Agw.Shared.Exceptions;
using Agw.Tools.Impl.Basic;

namespace Agw.Tools.Tests;

#pragma warning disable CS0618 // BashTool remains supported for legacy tool definitions.

public class BashToolTests
{
    [Fact]
    public void Execute_CommandMissing_ThrowsCommandRequired()
    {
        var exception = Assert.Throws<AgwException>(() =>
            new BashTool().Execute(new BashToolParams()));

        Assert.Equal(ErrorCodes.CommandRequired.Code, exception.Code);
    }

    [Fact]
    public void Execute_CommandFails_ReturnsNonZeroExitCodeWithoutToolError()
    {
        var result = new BashTool().Execute(new BashToolParams
        {
            Command = OperatingSystem.IsWindows()
                ? "echo command failed 1>&2 & exit /b 7"
                : "echo 'command failed' >&2; exit 7"
        });

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("command failed", result.Stderr);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Execute_ProcessCannotStart_ReturnsCommandExecutionFailedResult()
    {
        var missingShell = Path.Combine(
            Path.GetTempPath(),
            $"agw-missing-shell-{Guid.NewGuid():N}");

        var result = new BashTool(missingShell).Execute(new BashToolParams
        {
            Command = "echo test"
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, result.ErrorCode);
        Assert.StartsWith("Failed to start process:", result.ErrorMessage);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public void Execute_Timeout_ReturnsCommandTimeoutResult()
    {
        var result = new BashTool().Execute(new BashToolParams
        {
            Command = OperatingSystem.IsWindows()
                ? "ping 127.0.0.1 -n 6 > nul"
                : "sleep 5",
            Timeout = 50
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(ErrorCodes.CommandTimeout.Code, result.ErrorCode);
        Assert.Equal("Command timed out after 50ms.", result.ErrorMessage);
        Assert.True(result.DurationMs >= 0);
    }
}

#pragma warning restore CS0618
