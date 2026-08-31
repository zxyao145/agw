using System.Text.Json;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class PiProcessTargetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_DirectExecutable_PreservesTargetAndArguments(bool isWindows)
    {
        // Arrange
        var arguments = new[] { "--mode", "rpc" };

        // Act
        var target = PiProcessTarget.Resolve("pi.exe", arguments, isWindows);

        // Assert
        Assert.Equal("pi.exe", target.FileName);
        Assert.Equal(arguments, target.ArgumentList);
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void Resolve_WindowsGlobalNpmShim_UsesNodeAndPreservesArguments(string extension)
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"pi-target-{Guid.CreateVersion7():N}");
        var script = Path.Combine(root, $"pi{extension}");
        var node = Path.Combine(root, "node.exe");
        var packageRoot = Path.Combine(root, "node_modules", "@earendil-works", "pi-coding-agent");
        var entrypoint = Path.Combine(packageRoot, "dist", "bundle", "cli.js");
        var arguments = new[] { "--session-name", "100% & (quoted \"name\")" };
        CreateNpmLayout(script, node, packageRoot, entrypoint);

        try
        {
            // Act
            var target = PiProcessTarget.Resolve(script, arguments, isWindows: true);

            // Assert
            Assert.Equal(node, target.FileName);
            Assert.Equal([entrypoint, .. arguments], target.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_WindowsLocalNpmShim_UsesSiblingPackage()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"pi-target-{Guid.CreateVersion7():N}");
        var binDirectory = Path.Combine(root, "node_modules", ".bin");
        var script = Path.Combine(binDirectory, "pi.cmd");
        var packageRoot = Path.Combine(root, "node_modules", "@earendil-works", "pi-coding-agent");
        var entrypoint = Path.Combine(packageRoot, "dist", "bundle", "cli.js");
        CreateNpmLayout(script, node: null, packageRoot, entrypoint);

        try
        {
            // Act
            var target = PiProcessTarget.Resolve(script, ["--mode", "rpc"], isWindows: true);

            // Assert
            Assert.Equal("node", target.FileName);
            Assert.Equal([entrypoint, "--mode", "rpc"], target.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_UnrecognizedWindowsCommandScript_FailsClosed()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"pi-target-{Guid.CreateVersion7():N}");
        var script = Path.Combine(root, "pi.cmd");
        Directory.CreateDirectory(root);
        File.WriteAllText(script, "@echo off");

        try
        {
            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                PiProcessTarget.Resolve(script, ["--mode", "rpc"], isWindows: true)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateNpmLayout(string script, string? node, string packageRoot, string entrypoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entrypoint)!);
        File.WriteAllText(script, "@echo off");
        File.WriteAllText(entrypoint, "");
        if (node != null)
        {
            File.WriteAllText(node, "");
        }

        var package = JsonSerializer.Serialize(
            new { bin = new Dictionary<string, string> { ["pi"] = "dist/bundle/cli.js" } }
        );
        File.WriteAllText(Path.Combine(packageRoot, "package.json"), package);
    }
}
