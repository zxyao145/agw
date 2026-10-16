using Agw.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace Agw.Infrastructure.Tests;

public sealed class SqliteJournalModeTests : IDisposable
{
    private readonly SqliteTestDatabase _database = SqliteTestDatabase.Create("journal-mode");

    [Fact]
    public void EnsureWriteAheadLog_MissingFile_CreatesWalDatabase()
    {
        // Act
        var actual = SqliteJournalMode.EnsureWriteAheadLog(_database.ConnectionString());

        // Assert
        Assert.Equal(_database.FilePath, actual);
        Assert.True(File.Exists(_database.FilePath));
        Assert.Equal("wal", _database.ReadJournalMode());
    }

    [Fact]
    public void EnsureWriteAheadLog_RollbackJournalDatabase_ConvertsAndKeepsData()
    {
        // Arrange
        CreateRollbackJournalDatabase();

        // Act
        SqliteJournalMode.EnsureWriteAheadLog(_database.ConnectionString());

        // Assert
        Assert.Equal("wal", _database.ReadJournalMode());
        Assert.Equal("kept", _database.ExecuteScalar("SELECT value FROM marker"));
    }

    [Fact]
    public void EnsureWriteAheadLog_WalDatabase_KeepsWalMode()
    {
        // Arrange
        SqliteJournalMode.EnsureWriteAheadLog(_database.ConnectionString());

        // Act
        var actual = SqliteJournalMode.EnsureWriteAheadLog(_database.ConnectionString());

        // Assert
        Assert.Equal(_database.FilePath, actual);
        Assert.Equal("wal", _database.ReadJournalMode());
    }

    [Fact]
    public void EnsureWriteAheadLog_MemoryDatabase_ReturnsNull()
    {
        // Act
        var dataSource = SqliteJournalMode.EnsureWriteAheadLog("Data Source=:memory:");
        var mode = SqliteJournalMode.EnsureWriteAheadLog($"Data Source={_database.FilePath};Mode=Memory");

        // Assert
        Assert.Null(dataSource);
        Assert.Null(mode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_database.DirectoryPath));
    }

    [Fact]
    public void EnsureWriteAheadLog_ReadOnlyDirectory_ThrowsSqliteException()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        CreateRollbackJournalDatabase();
        File.SetUnixFileMode(_database.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Act
            var exception = Assert.Throws<SqliteException>(() =>
                SqliteJournalMode.EnsureWriteAheadLog(_database.ConnectionString())
            );

            // Assert: SQLITE_READONLY_DIRECTORY
            Assert.Equal(1544, exception.SqliteExtendedErrorCode);
        }
        finally
        {
            File.SetUnixFileMode(
                _database.DirectoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    public void Dispose() => _database.Dispose();

    private void CreateRollbackJournalDatabase()
    {
        Assert.Equal("delete", _database.ExecuteScalar("PRAGMA journal_mode = DELETE"));
        _database.ExecuteScalar("CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker VALUES ('kept');");
        Assert.Equal("delete", _database.ReadJournalMode());
    }
}
