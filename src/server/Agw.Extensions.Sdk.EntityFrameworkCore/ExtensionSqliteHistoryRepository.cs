using System.Data.Common;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Migrations.Internal;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

#pragma warning disable EF1001
internal sealed class ExtensionSqliteHistoryRepository : SqliteHistoryRepository
{
    private readonly string _lockTableName;

    public ExtensionSqliteHistoryRepository(
        HistoryRepositoryDependencies dependencies,
        ExtensionDatabaseSchemaMetadata options
    )
        : base(dependencies)
    {
        _lockTableName = $"{options.TablePrefix}__EFMigrationsLock";
    }

    protected override string LockTableName => _lockTableName;

    internal async Task ClearAbandonedLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = @name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = LockTableName;
        command.Parameters.Add(parameter);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            return;

        // 调用方持有平台的排他迁移锁，此时可以清理中断进程保留的 EF lock row。
        // The caller holds the platform's exclusive migration lock, so an interrupted process's EF lock row can be cleared.
        command.Parameters.Clear();
        command.CommandText = $"DELETE FROM {Dependencies.SqlGenerationHelper.DelimitIdentifier(LockTableName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
#pragma warning restore EF1001
