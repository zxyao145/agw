using System.Diagnostics;
using System.Runtime.CompilerServices;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

/// <summary>
/// 使用固定版本的 npm Pi、实际 Node.js 和 Unix login shell 验证进程启动环境。
/// Verifies process startup environments with the pinned npm Pi, a real Node.js, and a Unix login shell.
/// </summary>
public sealed class PiCliStartupTests : IDisposable
{
    private const string CliVersion = "0.84.4";
    private const string SystemPath = "/usr/bin:/bin:/usr/sbin:/sbin";

    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "cli startup tests",
        Guid.CreateVersion7().ToString("N")
    );

    public PiCliStartupTests() => Directory.CreateDirectory(_directory);

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "此测试需要 Unix shell。This test requires a Unix shell.", SkipWhen = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStartInfoAsync_LoginShellEnvironment_StartsNpmPi(bool relativePath)
    {
        // 准备测试数据。Arrange.
        var environment = await CreateEnvironmentAsync(relativePath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var workingDirectory = Path.Combine(_directory, "workspace");
        await using var transport = CreateTransport(new PiAgentOptions(), environment, workingDirectory);

        // 准备进程。Act.
        var start = await transport.CreateStartInfoAsync(TestContext.Current.CancellationToken);

        // 验证程序入口、环境和实际启动结果。Assert.
        Assert.Equal(Path.Combine(_directory, "cli tools", "pi"), start.FileName);
        Assert.Equal(workingDirectory, start.WorkingDirectory);
        Assert.Equal(ExpectedPath(_directory), start.Environment["PATH"]);
        Assert.Equal("execution-token", start.Environment["AGW_TOKEN"]);
        Assert.False(start.Environment.ContainsKey("PI_SHELL_ONLY"));
        await AssertVersionAsync(start);
        Assert.Equal("", environment["PATH"]);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH"));
    }

    [Fact(Skip = "此测试需要 Unix shell。This test requires a Unix shell.", SkipWhen = nameof(IsWindows))]
    public async Task CreateStartInfoAsync_InheritedLoginShellEnvironment_StartsNpmPi()
    {
        // 准备测试数据。Arrange.
        var environment = await CreateEnvironmentAsync(relativePath: true);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PiAgentSdk.Tests"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        start.ArgumentList.Add("-explicit");
        start.ArgumentList.Add("only");
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(PiCliStartupTests).FullName}.{nameof(InheritedEnvironmentProbe_StartsNpmPi)}");
        start.ArgumentList.Add("-noLogo");

        // 在继承受控环境的子测试进程中运行探针。Act.
        var result = await RunProcessAsync(start);

        // 验证探针通过。Assert.
        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
    }

    // 独立测试进程继承受控环境，避免修改并行测试共享的进程环境。
    // The isolated test process inherits a controlled environment without changing shared test state.
    [Fact(Explicit = true)]
    public async Task InheritedEnvironmentProbe_StartsNpmPi()
    {
        // 准备测试数据。Arrange.
        var home = Environment.GetEnvironmentVariable("HOME")!;
        await using var transport = CreateTransport(
            new PiAgentOptions(),
            environment: null,
            Path.Combine(home, "workspace")
        );

        // 准备进程。Act.
        var start = await transport.CreateStartInfoAsync(TestContext.Current.CancellationToken);

        // 验证程序入口、环境和实际启动结果。Assert.
        Assert.Equal(Path.Combine(home, "cli tools", "pi"), start.FileName);
        await AssertVersionAsync(start);
        Assert.Equal(ExpectedPath(home), start.Environment["PATH"]);
        Assert.Equal("", Environment.GetEnvironmentVariable("PATH"));
    }

    [Fact(Skip = "此测试需要 Unix shell。This test requires a Unix shell.", SkipWhen = nameof(IsWindows))]
    public async Task NpmLauncher_EmptyPath_ReportsMissingNode()
    {
        // 准备测试数据。Arrange.
        var environment = await CreateEnvironmentAsync(relativePath: false);
        var start = new ProcessStartInfo(Path.Combine(_directory, "cli tools", "pi"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        start.ArgumentList.Add("--version");

        // 用不含 Node 的 PATH 直接启动 npm 启动器。Act.
        var result = await RunProcessAsync(start);

        // 验证 env 找不到 node。Assert.
        Assert.Equal(127, result.ExitCode);
        Assert.Contains("node", result.Error);
    }

    [Fact(Skip = "此测试需要 Unix shell。This test requires a Unix shell.", SkipWhen = nameof(IsWindows))]
    public async Task CreateStartInfoAsync_ExplicitNpmPath_StartsWithProvidedEnvironment()
    {
        // 准备测试数据。Arrange.
        var environment = await CreateEnvironmentAsync(relativePath: false);
        environment["PATH"] = ExpectedPath(_directory);
        var executable = Path.Combine(_directory, "cli tools", "pi");
        await using var transport = CreateTransport(
            new PiAgentOptions { PiPathOverride = executable },
            environment,
            workingDirectory: null
        );

        // 准备进程。Act.
        var start = await transport.CreateStartInfoAsync(TestContext.Current.CancellationToken);

        // 验证显式入口使用提供的环境。Assert.
        Assert.Equal(executable, start.FileName);
        Assert.Equal(environment["PATH"], start.Environment["PATH"]);
        await AssertVersionAsync(start);
    }

    private static PiProcessTransport CreateTransport(
        PiAgentOptions agentOptions,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory
    ) =>
        new(
            agentOptions,
            new PiSessionOptions { WorkingDirectory = workingDirectory, EnvironmentVariables = environment },
            resumeSessionId: null,
            logger: null
        );

    private async Task<Dictionary<string, string>> CreateEnvironmentAsync(bool relativePath)
    {
        var token = TestContext.Current.CancellationToken;
        var launcher = Path.Combine(
            FixtureDirectory(),
            "node_modules",
            "@earendil-works",
            "pi-coding-agent",
            "dist",
            "bundle",
            "cli.js"
        );
        Assert.True(
            File.Exists(launcher),
            "Run npm ci --prefix src/sdks/pi-agent-sdk-csharp/tests/fixtures/pi-cli before the CLI startup tests."
        );
        var node = await CommandUtil.FindAsync("node", new CommandLookupOptions { UseLoginShell = false }, token);
        Assert.NotNull(node.Path);
        var cliDirectory = Path.Combine(_directory, "cli tools");
        var nodeDirectory = Path.Combine(_directory, "node tools");
        Directory.CreateDirectory(cliDirectory);
        Directory.CreateDirectory(nodeDirectory);
        Directory.CreateDirectory(Path.Combine(_directory, "workspace"));
        File.CreateSymbolicLink(Path.Combine(cliDirectory, "pi"), launcher);
        File.CreateSymbolicLink(Path.Combine(nodeDirectory, "node"), node.Path);
        var shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        var shellConfiguration = OperatingSystem.IsMacOS() ? ".zshrc" : ".bash_profile";
        var path = relativePath
            ? $"cli tools:node tools::{SystemPath}"
            : $"$HOME/cli tools:$HOME/node tools:{SystemPath}";
        await File.WriteAllTextAsync(
            Path.Combine(_directory, shellConfiguration),
            $"cd \"$HOME\"\nexport PATH=\"{path}\"\nexport PI_SHELL_ONLY=shell-value\n",
            token
        );
        return new Dictionary<string, string>
        {
            ["PATH"] = "",
            ["HOME"] = _directory,
            ["ZDOTDIR"] = _directory,
            ["SHELL"] = shell,
            ["AGW_TOKEN"] = "execution-token",
        };
    }

    private static string ExpectedPath(string directory) =>
        $"{Path.Combine(directory, "cli tools")}:{Path.Combine(directory, "node tools")}:{SystemPath}";

    private static string FixtureDirectory([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath("../fixtures/pi-cli", Path.GetDirectoryName(sourceFile)!);

    private static async Task AssertVersionAsync(ProcessStartInfo start)
    {
        start.ArgumentList.Clear();
        start.ArgumentList.Add("--version");
        var result = await RunProcessAsync(start);
        Assert.True(result.ExitCode == 0, $"Exit code {result.ExitCode}: {result.Error}");
        Assert.Equal(CliVersion, result.Output.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo start)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
