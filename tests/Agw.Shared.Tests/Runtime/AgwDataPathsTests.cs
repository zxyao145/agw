using System.Diagnostics;
using System.Text;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;

namespace Agw.Shared.Tests.Runtime;

public class AgwDataPathsTests
{
    [Fact]
    public void Resolve_WhenOverrideIsMissing_UsesSystemApplicationDataDirectory()
    {
        var applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify
        );
        var paths = AgwDataPaths.Resolve(null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.Equal(Path.Combine(applicationData, OperatingSystem.IsLinux() ? "agw" : "Agw"), paths.Root);
        Assert.True(Path.IsPathFullyQualified(paths.Root));
        Assert.True(Path.IsPathFullyQualified(paths.LogsDirectory));
        Assert.Equal(Path.Combine(paths.Root, "database", "agw.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(paths.Root, "skills"), paths.SkillsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "temp"), paths.TempDirectory);
        Assert.Equal(Path.Combine(paths.Root, "keys"), paths.KeysDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime"), paths.RuntimeDirectory);
        Assert.Equal(Path.Combine(paths.Root, "runtime", "server.json"), paths.ServerRuntimeFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveFromConfiguration_EmptyRoot_UsesSystemApplicationDataDirectory(string? root)
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
    }

    [Theory]
    [InlineData("~someone/agw")]
    [InlineData("./~/agw")]
    public void Resolve_TildeOutsideHomePrefix_PreservesRelativePath(string configuredRoot)
    {
        Assert.Equal(Path.GetFullPath(configuredRoot), AgwDataPaths.Resolve(configuredRoot, "/unused").Root);
    }

    public static TheoryData<string, bool, string?, string> LogDirectoryCases()
    {
        var absoluteRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "absolute-data"));
        return new()
        {
            { "./separate-data", false, null, "./separate-data/logs" },
            { "./separate-data", true, null, "./separate-data/logs" },
            { "./separate-data", true, "", "./separate-data/logs" },
            { "./separate-data", true, " ", "./separate-data/logs" },
            { "./separate-data", true, "./custom-logs", "./custom-logs" },
            { absoluteRoot, false, null, Path.Combine(absoluteRoot, "logs") },
            { absoluteRoot, true, " ", Path.Combine(absoluteRoot, "logs") },
        };
    }

    [Theory]
    [MemberData(nameof(LogDirectoryCases))]
    public void ResolveFromConfiguration_LogDirectory_UsesConfiguredOrDataRootDirectory(
        string root,
        bool hasLogKey,
        string? logs,
        string expected
    )
    {
        var values = new Dictionary<string, string?> { ["AgwDataDir"] = root };
        if (hasLogKey)
            values["AgwLogDir"] = logs;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var paths = AgwDataPaths.ResolveFromConfiguration(configuration);

        Assert.Equal(Path.GetFullPath(expected), paths.LogsDirectory);
        Assert.Equal(Path.GetFullPath(root), paths.Root);
        Assert.True(Path.IsPathFullyQualified(paths.LogsDirectory));
    }

    [Fact]
    public void ResolveFromConfiguration_JsonAndCommandLine_UsesEffectiveLogDirectory()
    {
        using var json = new MemoryStream(
            Encoding.UTF8.GetBytes("""{"AgwDataDir":"./json-data","AgwLogDir":"./json-logs"}""")
        );
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(json);
        Assert.Equal(
            Path.GetFullPath("./json-logs"),
            AgwDataPaths.ResolveFromConfiguration(configuration).LogsDirectory
        );

        configuration.AddCommandLine(["--AgwLogDir=./command-line-logs"]);
        var paths = AgwDataPaths.ResolveFromConfiguration(configuration);

        Assert.Equal(Path.GetFullPath("./command-line-logs"), paths.LogsDirectory);
        Assert.Equal(Path.GetFullPath("./json-data"), paths.Root);
    }

    [Fact]
    public void Resolve_LogDirectoryContainsSpacesAndUnicode_PreservesPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "数据 目录 & data"));
        var logs = Path.Combine(root, "日志 目录 & logs");

        var paths = AgwDataPaths.Resolve(root, "/unused", logs);

        Assert.Equal(root, paths.Root);
        Assert.Equal(logs, paths.LogsDirectory);
        Assert.Equal(Path.Combine(root, "logs"), AgwDataPaths.Resolve(root, "/unused").LogsDirectory);
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
    public void ResolveFromConfiguration_LegacyAlias_IsIgnored()
    {
        var prefix = $"AGW_TEST_{Guid.CreateVersion7():N}_";
        Environment.SetEnvironmentVariable(prefix + "AGW_DATA_DIR", "./old-data");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AgwDataDir"] = "./current-data" })
                .AddEnvironmentVariables(prefix)
                .Build();
            Assert.Equal(Path.GetFullPath("./current-data"), AgwDataPaths.ResolveFromConfiguration(configuration).Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "AGW_DATA_DIR", null);
        }
    }

    [Fact]
    public void EnsureCreated_WhenCalledRepeatedly_PreservesExistingFiles()
    {
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".scratch", $"agw-paths-{Guid.CreateVersion7():N}")
        );
        var paths = AgwDataPaths.Resolve(root, "/unused", Path.Combine(root, "separate-logs"));

        try
        {
            paths.EnsureCreated();
            var existingFile = Path.Combine(paths.Root, "existing.txt");
            File.WriteAllText(existingFile, "existing");

            paths.EnsureCreated();

            Assert.Equal("existing", File.ReadAllText(existingFile));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureCreated_ExistingDirectoryPermissions_PreservesPermissions(bool logsAtRoot)
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = ScratchDirectory("existing-permissions");
        var logs = logsAtRoot ? root : Path.Combine(root, "logs");
        var shared =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute;
        try
        {
            var existing = new[] { root, logs }.Distinct().ToArray();
            foreach (var directory in existing)
            {
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory, shared);
            }
            var before = existing.ToDictionary(directory => directory, ReadOwnerGroupAndMode);
            var paths = AgwDataPaths.Resolve(root, "/unused", logs);

            paths.EnsureCreated();
            paths.EnsureCreated();

            foreach (var directory in existing)
            {
                Assert.Equal(shared, File.GetUnixFileMode(directory));
                Assert.Equal(before[directory], ReadOwnerGroupAndMode(directory));
            }
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(paths.KeysDirectory)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreated_NewDirectory_UsesPrivatePermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = ScratchDirectory("new-permissions");
        var separate = ScratchDirectory("new-separate-logs");
        var paths = AgwDataPaths.Resolve(root, "/unused", Path.Combine(separate, "nested", "logs"));
        try
        {
            paths.EnsureCreated();

            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            foreach (
                var directory in new[]
                {
                    paths.Root,
                    Path.GetDirectoryName(paths.DatabaseFile)!,
                    paths.SkillsDirectory,
                    paths.TempDirectory,
                    paths.KeysDirectory,
                    paths.RuntimeDirectory,
                    separate,
                    Path.Combine(separate, "nested"),
                    paths.LogsDirectory,
                }
            )
            {
                Assert.Equal(expected, File.GetUnixFileMode(directory));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(separate, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreated_LogDirectoryIsRegularFile_Throws()
    {
        var root = ScratchDirectory("log-file-conflict");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(root);
        File.WriteAllText(logs, "existing file");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        var processed = new List<string>();
        try
        {
            Assert.ThrowsAny<IOException>(() => paths.EnsureCreated(processed.Add));

            Assert.Equal("existing file", File.ReadAllText(logs));
            Assert.Equal(logs, processed[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreated_LogDirectoryWriteProbe_CleansUpProbeFile()
    {
        var root = ScratchDirectory("write-probe");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        try
        {
            paths.EnsureCreated();
            paths.EnsureCreated();

            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.LogsDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreated_LogDirectoryIsReadOnly_Throws()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        var root = ScratchDirectory("read-only-logs");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        Directory.CreateDirectory(paths.LogsDirectory);
        File.SetUnixFileMode(paths.LogsDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => paths.EnsureCreated());

            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.LogsDirectory));
        }
        finally
        {
            File.SetUnixFileMode(
                paths.LogsDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ScratchDirectory(string name)
    {
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".scratch", $"{name}-{Guid.CreateVersion7():N}")
        );
    }

    private static string ReadOwnerGroupAndMode(string path)
    {
        // stat 一次输出属主、用户组和权限位，测试只比较前后是否一致。
        // stat prints the owner, group, and mode in one value; the test only compares it before and after.
        string[] arguments = OperatingSystem.IsMacOS() ? ["-f", "%u:%g:%Lp", path] : ["-c", "%u:%g:%a", path];
        using var process = Process.Start(new ProcessStartInfo("stat", arguments) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }
}
