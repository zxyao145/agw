using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Agw.Shared.Tests.Runtime;

public sealed class AgwDataDirectoryMigrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _userHome;
    private readonly string _applicationData;

    public AgwDataDirectoryMigrationTests()
    {
        _directory = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                ".scratch",
                $"data-migration-{Guid.CreateVersion7():N}"
            )
        );
        _userHome = Path.Combine(_directory, "home");
        _applicationData = Path.Combine(_directory, "application-data");
        Directory.CreateDirectory(_userHome);
    }

    [Fact]
    public void EnsureCreated_LegacyDataExists_PreservesSqliteSkillsAndEncryptionKeys()
    {
        var legacyRoot = Path.Combine(_userHome, "agw");
        var database = Path.Combine(legacyRoot, "database", "agw.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE migration_probe (value TEXT); INSERT INTO migration_probe VALUES ('existing data');";
            command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Path.Combine(legacyRoot, "skills"));
        File.WriteAllText(Path.Combine(legacyRoot, "skills", "SKILL.md"), "existing skill");
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(legacyRoot, "keys")),
            builder => builder.SetApplicationName("Agw")
        );
        var protectedValue = provider.CreateProtector("migration-test").Protect("existing credential");
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.False(Directory.Exists(legacyRoot));
        Assert.Equal("existing skill", File.ReadAllText(Path.Combine(paths.SkillsDirectory, "SKILL.md")));
        using (var connection = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM migration_probe";
            Assert.Equal("existing data", command.ExecuteScalar());
        }
        var migratedProvider = DataProtectionProvider.Create(
            new DirectoryInfo(paths.KeysDirectory),
            builder => builder.SetApplicationName("Agw")
        );
        Assert.Equal(
            "existing credential",
            migratedProvider.CreateProtector("migration-test").Unprotect(protectedValue)
        );
        Assert.Equal(Path.Combine(paths.Root, "logs"), paths.LogsDirectory);
    }

    [Fact]
    public void EnsureCreated_BothDataDirectoriesExist_PreservesBothDirectories()
    {
        var legacyRoot = Path.Combine(_userHome, "agw");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "existing.txt"), "legacy");
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(Path.Combine(paths.Root, "existing.txt"), "current");

        Assert.Throws<AgwException>(paths.EnsureCreated);

        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "existing.txt")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(paths.Root, "existing.txt")));
    }

    [Fact]
    public void EnsureCreated_ExplicitDataDirectory_PreservesLegacyData()
    {
        var legacyRoot = Path.Combine(_userHome, "agw");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "existing.txt"), "legacy");
        var paths = AgwDataPaths.Resolve(Path.Combine(_directory, "configured"), _userHome);

        paths.EnsureCreated();

        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "existing.txt")));
        Assert.True(Directory.Exists(paths.Root));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void EnsureCreated_LegacyProcessIsRunning_PreservesLegacyData()
    {
        var runtimeDirectory = Path.Combine(_userHome, "agw", "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "server.json"),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedAt = TimeProvider.System.GetUtcNow() })
        );
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);

        Assert.Throws<AgwException>(paths.EnsureCreated);

        Assert.True(Directory.Exists(runtimeDirectory));
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void EnsureCreated_LegacyDescriptorHasReusedProcessId_MigratesData()
    {
        var runtimeDirectory = Path.Combine(_userHome, "agw", "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "server.json"),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedAt = DateTimeOffset.UnixEpoch })
        );
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);

        paths.EnsureCreated();

        Assert.False(Directory.Exists(runtimeDirectory));
        Assert.True(File.Exists(paths.ServerRuntimeFile));
    }

    [Fact]
    public void EnsureCreated_EmptyLegacyDirectory_PreservesCurrentData()
    {
        Directory.CreateDirectory(Path.Combine(_userHome, "agw"));
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(Path.Combine(paths.Root, "existing.txt"), "current");

        paths.EnsureCreated();

        Assert.Equal("current", File.ReadAllText(Path.Combine(paths.Root, "existing.txt")));
    }

    [Fact]
    public void EnsureCreated_LegacyDirectoryIsSymbolicLink_PreservesLinkedData()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = Path.Combine(_directory, "linked-data");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "linked");
        Directory.CreateSymbolicLink(Path.Combine(_userHome, "agw"), target);
        var paths = AgwDataPaths.Resolve(null, _userHome, localApplicationDataDirectory: _applicationData);

        Assert.Throws<AgwException>(paths.EnsureCreated);

        Assert.Equal("linked", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.False(Directory.Exists(paths.Root));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
