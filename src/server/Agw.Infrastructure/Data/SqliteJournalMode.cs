using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;

namespace Agw.Infrastructure.Data;

/// <summary>
/// 在 Server 第一次访问数据库之前确立 SQLite 文件数据库的 journal mode。
/// Establishes the journal mode of a SQLite file database before the Server first accesses the database.
/// </summary>
public static class SqliteJournalMode
{
    /// <summary>
    /// 把文件数据库切换为 WAL 并返回数据库文件路径；内存数据库返回 null。
    /// Switches a file database to WAL and returns its file path; returns null for an in-memory database.
    /// </summary>
    public static string? EnsureWriteAheadLog(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (
            builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
        )
            return null;

        // Microsoft.Data.Sqlite 的 async 方法同步执行，这一步在启动时只执行一次，使用同步 API。
        // Microsoft.Data.Sqlite async methods run synchronously; this runs once at startup, so it uses the sync API.
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = (string)command.ExecuteScalar()!;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                $"SQLite database '{builder.DataSource}' cannot use WAL journal mode (journal mode: {mode}). Place the database on a local file system."
            );
        return builder.DataSource;
    }
}
