using System.Text;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;

namespace Agw.Shared.Tests.Runtime;

public class AgwDataPathsTests
{
    [Theory]
    [InlineData("/Users/tester")]
    [InlineData("/home/tester")]
    [InlineData("C:\\Users\\tester")]
    public void Resolve_WhenOverrideIsMissing_UsesLowercaseAgwUnderUserHome(string userHome)
    {
        var paths = AgwDataPaths.Resolve(null, userHome);

        Assert.Equal(Path.GetFullPath(Path.Combine(userHome, "agw")), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "server-state.json"), paths.StateFile);
        Assert.Equal(Path.Combine(paths.Root, "database", "agw.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(paths.Root, "skills"), paths.SkillsDirectory);
        Assert.Equal(Path.GetFullPath("./logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "temp"), paths.TempDirectory);
        Assert.Equal(Path.Combine(paths.Root, "keys"), paths.KeysDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime"), paths.RuntimeDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime", "server.json"), paths.ServerRuntimeFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveFromConfiguration_EmptyRoot_UsesHomeDirectory(string? root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgwDataDir"] = root })
            .Build();

        Assert.Equal(
            AgwDataPaths.Resolve(null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Root,
            AgwDataPaths.ResolveFromConfiguration(configuration).Root
        );
    }

    [Theory]
    [InlineData("AgwDataDir")]
    [InlineData("AGW_DATA_DIR")]
    public void ResolveFromConfiguration_JsonEnvironmentAndCommandLine_UseStandardPrecedence(string environmentKey)
    {
        var prefix = $"AGW_TEST_{Guid.CreateVersion7():N}_";
        Environment.SetEnvironmentVariable(prefix + environmentKey, "./environment-data");
        try
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes("""{"AgwDataDir":"./json-data"}"""));
            var builder = new ConfigurationBuilder().AddJsonStream(json);
            var fromJson = builder.Build();
            Assert.Equal(Path.GetFullPath("./json-data"), AgwDataPaths.ResolveFromConfiguration(fromJson).Root);

            var fromEnvironment = new ConfigurationBuilder()
                .AddConfiguration(fromJson)
                .AddEnvironmentVariables(prefix)
                .Build();
            Assert.Equal(
                Path.GetFullPath("./environment-data"),
                AgwDataPaths.ResolveFromConfiguration(fromEnvironment).Root
            );

            var fromCommandLine = new ConfigurationBuilder()
                .AddConfiguration(fromJson)
                .AddEnvironmentVariables(prefix)
                .AddCommandLine(["--AgwDataDir=./command-line-data"])
                .Build();
            Assert.Equal(
                Path.GetFullPath("./command-line-data"),
                AgwDataPaths.ResolveFromConfiguration(fromCommandLine).Root
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + environmentKey, null);
        }
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/agw", "agw")]
    [InlineData("~\\agw", "agw")]
    [InlineData("  ~/custom-data  ", "custom-data")]
    public void Resolve_TildeRoot_ExpandsAgainstProvidedHome(string configuredRoot, string suffix)
    {
        var home = Path.Combine(Path.GetTempPath(), "agw-test-home");

        var paths = AgwDataPaths.Resolve(configuredRoot, home);

        Assert.Equal(Path.GetFullPath(Path.Combine(home, suffix)), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "server-state.json"), paths.StateFile);
    }

    [Theory]
    [InlineData("~someone/agw")]
    [InlineData("./~/agw")]
    public void Resolve_TildeOutsideHomePrefix_PreservesRelativePath(string configuredRoot)
    {
        Assert.Equal(Path.GetFullPath(configuredRoot), AgwDataPaths.Resolve(configuredRoot, "/unused").Root);
    }

    [Theory]
    [InlineData(null, "./logs")]
    [InlineData("", "./logs")]
    [InlineData(" ", "./logs")]
    [InlineData("./custom-logs", "./custom-logs")]
    public void ResolveFromConfiguration_LogDirectory_IsIndependentOfDataRoot(string? logs, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["AgwDataDir"] = "./separate-data", ["AgwLogDir"] = logs }
            )
            .Build();

        var paths = AgwDataPaths.ResolveFromConfiguration(configuration);

        Assert.Equal(Path.GetFullPath(expected), paths.LogsDirectory);
        Assert.Equal(Path.GetFullPath("./separate-data"), paths.Root);
    }

    [Fact]
    public void Resolve_LogDirectoryWithTilde_UsesUserHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "test-home");
        var paths = AgwDataPaths.Resolve("./data", home, "~/my-logs");

        Assert.Equal(Path.Combine(home, "my-logs"), paths.LogsDirectory);
    }

    [Fact]
    public void Resolve_WhenOverrideIsProvided_UsesOverride()
    {
        var paths = AgwDataPaths.Resolve("./custom-data", "/Users/tester");

        Assert.Equal(Path.GetFullPath("./custom-data"), paths.Root);
    }

    [Fact]
    public void ResolveFromEnvironment_WhenOverrideIsSet_UsesEnvironmentValue()
    {
        var original = Environment.GetEnvironmentVariable("AGW_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(), $"agw-env-{Guid.CreateVersion7():N}");
        try
        {
            Environment.SetEnvironmentVariable("AGW_DATA_DIR", root);

            Assert.Equal(Path.GetFullPath(root), AgwDataPaths.ResolveFromEnvironment().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGW_DATA_DIR", original);
        }
    }

    [Fact]
    public void EnsureCreated_WhenCalledRepeatedly_PreservesExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-paths-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused", Path.Combine(root, "separate-logs"));

        try
        {
            paths.EnsureCreated();
            File.WriteAllText(paths.StateFile, "existing");

            paths.EnsureCreated();

            Assert.Equal("existing", File.ReadAllText(paths.StateFile));
            Assert.True(Directory.Exists(Path.GetDirectoryName(paths.DatabaseFile)));
            Assert.True(Directory.Exists(paths.SkillsDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.TempDirectory));
            Assert.True(Directory.Exists(paths.KeysDirectory));
            Assert.True(Directory.Exists(paths.RuntimeDirectory));
            if (!OperatingSystem.IsWindows())
            {
                var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Assert.Equal(expected, File.GetUnixFileMode(paths.Root));
                Assert.Equal(expected, File.GetUnixFileMode(paths.KeysDirectory));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
