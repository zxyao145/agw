using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Nito.AsyncEx;

namespace Agw.Extensions.Infrastructure.Database;

public sealed class ExtensionDatabaseLockCoordinator : IExtensionDatabaseTransactionCoordinator
{
    private static readonly ConcurrentDictionary<SqliteLockKey, AsyncReaderWriterLock> SqliteLocks = new();

    public async ValueTask<IAsyncDisposable> AcquireBusinessAccessAsync(
        Guid extensionId,
        ExtensionDatabaseProvider provider,
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken = default
    )
    {
        if (provider == ExtensionDatabaseProvider.Sqlite)
        {
            var key = new SqliteLockKey(GetSqliteDatabaseIdentity(connection), extensionId);
            IDisposable readerLock = await GetSqliteLock(key).ReaderLockAsync(cancellationToken);
            return new SyncDisposableLease(readerLock);
        }

        if (provider == ExtensionDatabaseProvider.PostgreSql)
        {
            if (transaction is null || transaction.Connection != connection)
            {
                throw new AgwException(ErrorCodes.InvalidParam);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock_shared(@lock_key)";
            AddLockKey(command, GetPostgresLockKey(extensionId));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return NoopAsyncDisposable.Instance;
        }

        throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider);
    }

    public async ValueTask<IAsyncDisposable> AcquireMigrationAccessAsync(
        Guid extensionId,
        ExtensionDatabaseProvider provider,
        DbConnection connection,
        CancellationToken cancellationToken = default
    )
    {
        if (provider == ExtensionDatabaseProvider.Sqlite)
        {
            var key = new SqliteLockKey(GetSqliteDatabaseIdentity(connection), extensionId);
            IDisposable writerLock = await GetSqliteLock(key).WriterLockAsync(cancellationToken);
            if (
                key.DatabaseIdentity.Length == 0
                || key.DatabaseIdentity == ":memory:"
                || key.DatabaseIdentity.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            )
                return new SyncDisposableLease(writerLock);
            try
            {
                FileStream ownership;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        ownership = new FileStream(
                            $"{key.DatabaseIdentity}.{extensionId:N}.migration.lock",
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None
                        );
                        break;
                    }
                    catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 or 35)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
                return new SqliteMigrationLease(writerLock, ownership);
            }
            catch
            {
                writerLock.Dispose();
                throw;
            }
        }

        if (provider == ExtensionDatabaseProvider.PostgreSql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(@lock_key)";
            AddLockKey(command, GetPostgresLockKey(extensionId));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new PostgresSessionLockLease(connection, GetPostgresLockKey(extensionId));
        }

        throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider);
    }

    internal static long GetPostgresLockKey(Guid extensionId)
    {
        byte[] hash = SHA256.HashData(extensionId.ToByteArray());
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private static AsyncReaderWriterLock GetSqliteLock(SqliteLockKey key) =>
        SqliteLocks.GetOrAdd(key, static _ => new AsyncReaderWriterLock());

    private static string GetSqliteDatabaseIdentity(DbConnection connection)
    {
        string dataSource = connection.DataSource;
        if (
            dataSource.Length == 0
            || dataSource == ":memory:"
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        )
        {
            return dataSource;
        }

        return Path.GetFullPath(dataSource);
    }

    private static void AddLockKey(DbCommand command, long lockKey)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@lock_key";
        parameter.DbType = DbType.Int64;
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);
    }

    private sealed record SqliteLockKey(string DatabaseIdentity, Guid ExtensionId);

    private sealed class SyncDisposableLease : IAsyncDisposable
    {
        private IDisposable? _lease;

        public SyncDisposableLease(IDisposable lease)
        {
            _lease = lease;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PostgresSessionLockLease : IAsyncDisposable
    {
        private readonly long _lockKey;
        private DbConnection? _connection;

        public PostgresSessionLockLease(DbConnection connection, long lockKey)
        {
            _connection = connection;
            _lockKey = lockKey;
        }

        public async ValueTask DisposeAsync()
        {
            DbConnection? connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@lock_key)";
            AddLockKey(command, _lockKey);
            if (await command.ExecuteScalarAsync() is not true)
            {
                throw new AgwException(
                    ErrorCodes.ExtensionMigrationFailed,
                    "The extension migration lock was not held."
                );
            }
        }
    }

    private sealed class SqliteMigrationLease(IDisposable writerLock, FileStream ownership) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ownership.DisposeAsync();
            }
            finally
            {
                writerLock.Dispose();
            }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
