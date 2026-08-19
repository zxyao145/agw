using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Skills.Execution;

namespace Agw.Skills.Tests;

public class LocalSkillScriptRunnerTests
{
    [Fact]
    public async Task RunAsync_ValidPythonScript_PassesArgumentsAndUsesSkillDirectory()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "inspect.py");
        await File.WriteAllTextAsync(Path.Combine(root, "marker.txt"), "marker", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            script,
            "import json, os, sys; print(json.dumps({'cwd': os.getcwd(), 'marker': os.path.isfile('marker.txt'), 'args': sys.argv[1:]}))",
            TestContext.Current.CancellationToken
        );
        using var arguments = JsonDocument.Parse("""["alpha beta","literal;value"]""");

        try
        {
            var result = Assert.IsType<string>(
                await LocalSkillScriptRunner.RunAsync(
                    root,
                    script,
                    arguments.RootElement,
                    TestContext.Current.CancellationToken
                )
            );
            using var output = JsonDocument.Parse(result);

            Assert.Equal(Path.GetFileName(root), Path.GetFileName(output.RootElement.GetProperty("cwd").GetString()));
            Assert.True(output.RootElement.GetProperty("marker").GetBoolean());
            Assert.Equal(
                ["alpha beta", "literal;value"],
                output.RootElement.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray()
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateStartInfo_JavaScriptScript_UsesNodeWithLiteralArguments()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "inspect.js");

        try
        {
            var startInfo = LocalSkillScriptRunner.CreateStartInfo(root, script, ["alpha beta", "literal;value"]);

            Assert.Equal("node", startInfo.FileName);
            Assert.Equal([script, "alpha beta", "literal;value"], startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateStartInfo_CSharpScript_UsesDotnetFileAppWithArgumentSeparator()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "inspect.cs");

        try
        {
            var startInfo = LocalSkillScriptRunner.CreateStartInfo(root, script, ["alpha beta", "literal;value"]);

            Assert.Equal("dotnet", startInfo.FileName);
            Assert.Equal(["run", "--file", script, "--", "alpha beta", "literal;value"], startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ScriptOutsideSkillDirectory_ThrowsCommandExecutionFailure()
    {
        var root = CreateTempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"agw-outside-{Guid.CreateVersion7():N}.py");
        await File.WriteAllTextAsync(outside, "print('outside')", TestContext.Current.CancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                LocalSkillScriptRunner.RunAsync(root, outside, arguments: null, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("script.csx")]
    [InlineData("script.sh")]
    [InlineData("script.ps1")]
    public async Task RunAsync_UnsupportedExtension_ThrowsCommandExecutionFailure(string fileName)
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, fileName);
        await File.WriteAllTextAsync(script, "unsupported", TestContext.Current.CancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                LocalSkillScriptRunner.RunAsync(root, script, arguments: null, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsCommandExecutionFailure()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "fail.py");
        await File.WriteAllTextAsync(script, "raise SystemExit(7)", TestContext.Current.CancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                LocalSkillScriptRunner.RunAsync(root, script, arguments: null, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
            Assert.Contains("7", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Timeout_ThrowsCommandTimeout()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "wait.py");
        await File.WriteAllTextAsync(script, "import time; time.sleep(10)", TestContext.Current.CancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                LocalSkillScriptRunner.RunAsync(
                    root,
                    script,
                    arguments: null,
                    TestContext.Current.CancellationToken,
                    timeout: TimeSpan.FromMilliseconds(100)
                )
            );

            Assert.Equal(ErrorCodes.CommandTimeout.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agw-local-skill-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
