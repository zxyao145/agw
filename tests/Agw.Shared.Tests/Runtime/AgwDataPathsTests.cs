using Agw.Shared.Runtime;

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
        Assert.Equal(Path.Combine(paths.Root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "temp"), paths.TempDirectory);
        Assert.Equal(Path.Combine(paths.Root, "keys"), paths.KeysDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime"), paths.RuntimeDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime", "server.json"), paths.ServerRuntimeFile);
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
        var paths = AgwDataPaths.Resolve(root, "/unused");

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
