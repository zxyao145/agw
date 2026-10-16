using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SQLitePCL;

namespace Agw.Infrastructure.Data.Interceptors;

/// <summary>
/// 每条命令执行之前、每个事务开始之前，把连接的原生 busy_timeout 设置为这个操作的有效超时。
/// Sets the connection's native busy_timeout to the operation's effective timeout before each command and each transaction start.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor : IDbCommandInterceptor, IDbTransactionInterceptor
{
    public static readonly SqliteBusyTimeoutInterceptor Instance = new();

    private SqliteBusyTimeoutInterceptor() { }

    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    )
    {
        ApplyCommand(command);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        ApplyCommand(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result
    )
    {
        ApplyCommand(command);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        ApplyCommand(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result
    )
    {
        ApplyCommand(command);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default
    )
    {
        ApplyCommand(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        ApplyTransactionStart(connection);
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default
    )
    {
        ApplyTransactionStart(connection);
        return ValueTask.FromResult(result);
    }

    // SqliteCommand 没有单独设置超时的时候返回连接当前的 DefaultTimeout。
    // SqliteCommand returns the connection's current DefaultTimeout when the command has no timeout of its own.
    private static void ApplyCommand(DbCommand command) => Apply(command.Connection!, command.CommandTimeout);

    // BEGIN IMMEDIATE 由 SqliteTransaction 内部创建的命令执行，使用连接当前的 DefaultTimeout。
    // BEGIN IMMEDIATE runs through a command created inside SqliteTransaction and uses the connection's current DefaultTimeout.
    private static void ApplyTransactionStart(DbConnection connection) =>
        Apply(connection, ((SqliteConnection)connection).DefaultTimeout);

    private static void Apply(DbConnection connection, int seconds)
    {
        var sqlite = (SqliteConnection)connection;
        var milliseconds = seconds == 0 ? int.MaxValue : (int)Math.Min(seconds * 1000L, int.MaxValue);
        SqliteException.ThrowExceptionForRC(raw.sqlite3_busy_timeout(sqlite.Handle!, milliseconds), sqlite.Handle);
    }
}
