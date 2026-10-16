using System.Diagnostics;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 测试使用的 SQLite 文件数据库：位于测试项目已被忽略的 .scratch 目录，连接不进入连接池。
/// A SQLite file database for tests: it lives in the test project's ignored .scratch directory, and its connections bypass the pool.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private SqliteTestDatabase(string directoryPath)
    {
        DirectoryPath = directoryPath;
        FilePath = Path.Combine(directoryPath, "agw.db");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public static SqliteTestDatabase Create(string name)
    {
        var directoryPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".scratch", $"{name}-{Guid.CreateVersion7():N}")
        );
        Directory.CreateDirectory(directoryPath);
        return new SqliteTestDatabase(directoryPath);
    }

    public string ConnectionString(string options = "") =>
        string.IsNullOrEmpty(options)
            ? $"Data Source={FilePath};Pooling=False"
            : $"Data Source={FilePath};Pooling=False;{options}";

    public static AgwDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Sqlite, connectionString);
        return new AgwDbContext(options.Options);
    }

    public object? ExecuteScalar(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public string ReadJournalMode() => Assert.IsType<string>(ExecuteScalar("PRAGMA journal_mode"));

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, recursive: true);
    }
}

/// <summary>
/// 在单独的连接上持有 SQLite 锁，到期或测试结束时释放。
/// Holds a SQLite lock on a separate connection and releases it when the hold expires or the test ends.
/// </summary>
internal sealed class SqliteLockHolder : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Action _release;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<long> _released;

    private SqliteLockHolder(SqliteConnection connection, Action release, TimeSpan hold)
    {
        _connection = connection;
        _release = release;
        _released = ReleaseAsync(hold);
    }

    /// <summary>
    /// 释放锁之前一刻的 Stopwatch 时间戳。
    /// The Stopwatch timestamp taken just before the lock is released.
    /// </summary>
    public Task<long> Released => _released;

    /// <summary>
    /// 对表执行 SELECT 并只读取第一行，语句保持进行中，释放时结束读取。
    /// Runs a SELECT on the table and reads only its first row, keeping the statement active until release.
    /// </summary>
    public static SqliteLockHolder Reader(string connectionString, string table, TimeSpan hold)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\"";
        var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new SqliteLockHolder(
            connection,
            () =>
            {
                reader.Dispose();
                command.Dispose();
            },
            hold
        );
    }

    /// <summary>
    /// 开启写入事务并执行修改，释放时提交。
    /// Opens a write transaction and runs the change, committing it on release.
    /// </summary>
    public static SqliteLockHolder Writer(string connectionString, string sql, TimeSpan hold)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return new SqliteLockHolder(
            connection,
            () =>
            {
                transaction.Commit();
                transaction.Dispose();
            },
            hold
        );
    }

    public async ValueTask DisposeAsync()
    {
        _stop.TrySetResult();
        await _released;
        await _connection.DisposeAsync();
    }

    private async Task<long> ReleaseAsync(TimeSpan hold)
    {
        await Task.WhenAny(Task.Delay(hold), _stop.Task);
        var releasedAt = Stopwatch.GetTimestamp();
        _release();
        return releasedAt;
    }
}
