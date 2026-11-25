using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpCommandResolverTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public async Task ResolveAsync_NameOnPath_ReturnsAbsolutePath()
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString()));
        try
        {
            var path = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "agent.exe" : "agent");
            File.WriteAllText(path, "");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = await AcpCommandResolver.ResolveAsync(
                "agent",
                new Dictionary<string, string> { ["PATH"] = directory.FullName, ["PATHEXT"] = ".exe" },
                TestContext.Current.CancellationToken
            );
            Assert.Equal(path, result.Path);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ResolveAsync_CommandWithSpaces_ThrowsStartFailed()
    {
        var ex = await Assert.ThrowsAsync<AgwException>(() =>
            AcpCommandResolver.ResolveAsync(
                "npx -y foo",
                new Dictionary<string, string>(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(ErrorCodes.AcpAgentStartFailed.Code, ex.Code);
        Assert.Equal("Command 'npx -y foo' was not found.", ex.Message);
    }

    [Fact(SkipUnless = nameof(Windows), Skip = "Requires Windows.")]
    public async Task ResolveAsync_WindowsCmdScript_ThrowsStartFailed()
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid()}.cmd");
        File.WriteAllText(file, "@exit");
        try
        {
            var ex = await Assert.ThrowsAsync<AgwException>(() =>
                AcpCommandResolver.ResolveAsync(
                    file,
                    new Dictionary<string, string>(),
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(ErrorCodes.AcpAgentStartFailed.Code, ex.Code);
            Assert.Contains(file, ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
