using System.Data.Common;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agw.Infrastructure.Tests;

public sealed class JobsEventTimerMigrationTests
{
    private const string PreviousMigrationName = "AddEventCenterAndNotifications";
    private const string MigrationName = "MoveJobsToEventTimers";
    private static readonly Guid ExtensionId = Guid.Parse("019a4c17-0000-7000-8000-00000000abcd");

    public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(IsolatedTestConnections.Postgres);

    [Fact]
    public async Task Upgrade_Sqlite_RemovesJobTablesAndAddsConversationKind()
    {
        var path = CreateDatabasePath();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            await UpgradeAsync(DatabaseProvider.Sqlite, connectionString);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    public async Task Upgrade_Postgres_RemovesJobTablesAndAddsConversationKind()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = new NpgsqlConnectionStringBuilder(IsolatedTestConnections.Postgres!) { Pooling = false };
        var database = $"agw_jobs_event_timer_migration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(token);
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = database,
            }.ConnectionString;
            await UpgradeAsync(DatabaseProvider.Postgres, connectionString);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task UpgradeAsync(DatabaseProvider provider, string connectionString)
    {
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(CreateOptions(provider, connectionString));
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationName, token);
        var appliedBeforeUpgrade = (await context.Database.GetAppliedMigrationsAsync(token)).ToArray();

        var jobId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        await SeedPreviousSchemaAsync(context, jobId, conversationId, subscriptionId, token);
        var migration = Assert.Single(
            context.Database.GetMigrations(),
            id => id.EndsWith($"_{MigrationName}", StringComparison.Ordinal)
        );
        await migrator.MigrateAsync(migration, token);
        var appliedAfterUpgrade = await context.Database.GetAppliedMigrationsAsync(token);
        Assert.Equal([migration], appliedAfterUpgrade.Except(appliedBeforeUpgrade));

        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(token);
        await AssertRemovedSchemaAsync(connection, provider, token);
        await AssertAddedSchemaAsync(connection, provider, conversationId, token);
        await AssertManagedRecordsAsync(connection, provider, subscriptionId, token);
        await context.Database.CloseConnectionAsync();

        await migrator.MigrateAsync(PreviousMigrationName, token);
        Assert.Equal(appliedBeforeUpgrade, await context.Database.GetAppliedMigrationsAsync(token));
        await context.Database.OpenConnectionAsync(token);
        await AssertRestoredSchemaAsync(connection, provider, token);
        await AssertRestoredManagedRecordsAsync(connection, provider, subscriptionId, token);
    }

    private static async Task SeedPreviousSchemaAsync(
        AgwDbContext context,
        Guid jobId,
        Guid conversationId,
        Guid subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var projectId = Guid.CreateVersion7();
        var createdAt = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO project_conversation (id, generation, project_id, context_id, title, job_id, create_by, create_time)
            VALUES ({conversationId}, {0}, {projectId}, {"existing-context"}, {"Existing"}, {jobId}, {"1001"}, {createdAt})
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO job (
                id, create_by, create_time, is_enabled, max_retry_count, name, next_run_time, project_id,
                retry_count, row_version, status, trigger_type, trigger_value
            )
            VALUES ({jobId}, {"1001"}, {createdAt}, {true}, {3}, {"Existing job"}, {createdAt}, {projectId},
                {0}, {new byte[] { 1 }}, {0}, {0}, {"once"})
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO job_log (id, job_id, attempt, create_by, create_time, start_time, success, task_id)
            VALUES ({Guid.CreateVersion7()}, {jobId}, {1}, {"1001"}, {createdAt}, {createdAt}, {true}, {Guid.CreateVersion7()})
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO event_subscription (
                id, name, enabled, source_ids_json, event_types_json, target_kind, handler_extension_id,
                managed_by_extension_id, managed_key, managed_version, managed_state, version, create_by, create_time
            )
            VALUES ({subscriptionId}, {"Extension subscription"}, {true}, {"[]"}, {"[]"}, {2}, {ExtensionId},
                {ExtensionId}, {"rule:1"}, {1L}, {0}, {1L}, {"1001"}, {createdAt})
            """,
            cancellationToken
        );
    }

    private static async Task AssertManagedRecordsAsync(
        DbConnection connection,
        DatabaseProvider provider,
        Guid subscriptionId,
        CancellationToken cancellationToken
    )
    {
        Assert.True(await ColumnExistsAsync(connection, provider, "event_source", "managed_by", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, provider, "event_source", "managed_key", cancellationToken));
        Assert.True(
            await ColumnExistsAsync(connection, provider, "event_subscription", "managed_by", cancellationToken)
        );
        Assert.False(
            await ColumnExistsAsync(
                connection,
                provider,
                "event_subscription",
                "managed_by_extension_id",
                cancellationToken
            )
        );
        foreach (var table in new[] { "event_source", "event_subscription" })
        {
            await AssertIndexAsync(
                connection,
                provider,
                table,
                $"ix_{table}_create_by_managed_by_managed_key",
                ["create_by", "managed_by", "managed_key"],
                true,
                "managed_key IS NOT NULL",
                cancellationToken
            );
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT managed_by, managed_key FROM event_subscription WHERE id = @id";
        AddParameter(command, "id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("ext:019a4c17-0000-7000-8000-00000000abcd", reader.GetString(0));
        Assert.Equal("rule:1", reader.GetString(1));
    }

    private static async Task AssertRestoredManagedRecordsAsync(
        DbConnection connection,
        DatabaseProvider provider,
        Guid subscriptionId,
        CancellationToken cancellationToken
    )
    {
        Assert.False(await ColumnExistsAsync(connection, provider, "event_source", "managed_by", cancellationToken));
        Assert.False(await ColumnExistsAsync(connection, provider, "event_source", "managed_key", cancellationToken));
        Assert.False(
            await ColumnExistsAsync(connection, provider, "event_subscription", "managed_by", cancellationToken)
        );
        await AssertIndexAsync(
            connection,
            provider,
            "event_subscription",
            provider == DatabaseProvider.Postgres
                ? "ix_event_subscription_create_by_managed_by_extension_id_manage"
                : "ix_event_subscription_create_by_managed_by_extension_id_managed_key",
            ["create_by", "managed_by_extension_id", "managed_key"],
            true,
            "managed_key IS NOT NULL",
            cancellationToken
        );

        // 用写入时的参数类型读取，确认还原后的值与 EF 写入的格式相同。
        // Compare through the parameter type used on insert so the restored value has the format EF writes.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM event_subscription WHERE id = @id AND managed_by_extension_id = @extensionId";
        AddParameter(command, "id", subscriptionId);
        AddParameter(command, "extensionId", ExtensionId);
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
    }

    private static async Task AssertRemovedSchemaAsync(
        DbConnection connection,
        DatabaseProvider provider,
        CancellationToken cancellationToken
    )
    {
        Assert.False(await TableExistsAsync(connection, provider, "job", cancellationToken));
        Assert.False(await TableExistsAsync(connection, provider, "job_log", cancellationToken));
        Assert.False(
            await ColumnExistsAsync(connection, provider, "project_conversation", "job_id", cancellationToken)
        );
        Assert.False(
            await ColumnExistsAsync(
                connection,
                provider,
                "project_conversation_chat_history",
                "job_id",
                cancellationToken
            )
        );
    }

    private static async Task AssertRestoredSchemaAsync(
        DbConnection connection,
        DatabaseProvider provider,
        CancellationToken cancellationToken
    )
    {
        Assert.True(await TableExistsAsync(connection, provider, "job", cancellationToken));
        Assert.True(await TableExistsAsync(connection, provider, "job_log", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, provider, "project_conversation", "job_id", cancellationToken));
        Assert.True(
            await ColumnExistsAsync(
                connection,
                provider,
                "project_conversation_chat_history",
                "job_id",
                cancellationToken
            )
        );
        Assert.False(await ColumnExistsAsync(connection, provider, "project_conversation", "kind", cancellationToken));
        Assert.False(
            await ColumnExistsAsync(connection, provider, "project_conversation", "relate_id", cancellationToken)
        );
        Assert.False(
            await ColumnExistsAsync(connection, provider, "event_delivery_attempt", "turn_id", cancellationToken)
        );
        await AssertIndexAsync(
            connection,
            provider,
            "project_conversation",
            "ix_project_conversation_job_id",
            ["job_id"],
            false,
            null,
            cancellationToken
        );
    }

    private static async Task AssertAddedSchemaAsync(
        DbConnection connection,
        DatabaseProvider provider,
        Guid conversationId,
        CancellationToken cancellationToken
    )
    {
        Assert.True(await TableExistsAsync(connection, provider, "event_source", cancellationToken));
        Assert.True(await TableExistsAsync(connection, provider, "event_subscription", cancellationToken));
        Assert.True(await TableExistsAsync(connection, provider, "event_delivery_attempt", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, provider, "project_conversation", "kind", cancellationToken));
        Assert.True(
            await ColumnExistsAsync(connection, provider, "project_conversation", "relate_id", cancellationToken)
        );
        Assert.True(
            await ColumnExistsAsync(connection, provider, "event_delivery_attempt", "turn_id", cancellationToken)
        );

        await AssertIndexAsync(
            connection,
            provider,
            "event_delivery_attempt",
            "ix_event_delivery_attempt_delivery_id_attempt",
            ["delivery_id", "attempt"],
            false,
            null,
            cancellationToken
        );
        await AssertIndexAsync(
            connection,
            provider,
            "event_delivery",
            "ix_event_delivery_subscription_id_id",
            ["subscription_id", "id"],
            false,
            null,
            cancellationToken
        );
        await AssertIndexAsync(
            connection,
            provider,
            "project_conversation",
            "ix_project_conversation_relate_id",
            ["relate_id"],
            false,
            null,
            cancellationToken
        );

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT context_id, title, kind, relate_id FROM project_conversation WHERE id = @id";
        AddParameter(command, "id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("existing-context", reader.GetString(0));
        Assert.Equal("Existing", reader.GetString(1));
        Assert.Equal("native", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
    }

    private static async Task AssertIndexAsync(
        DbConnection connection,
        DatabaseProvider provider,
        string tableName,
        string indexName,
        string[] expectedColumns,
        bool expectedUnique,
        string? expectedFilter,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @indexName",
            DatabaseProvider.Postgres => """
                SELECT indexdef FROM pg_indexes
                WHERE schemaname = current_schema() AND tablename = @tableName AND indexname = @indexName
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        AddParameter(command, "tableName", tableName);
        AddParameter(command, "indexName", indexName);
        var definition = (string?)await command.ExecuteScalarAsync(cancellationToken);
        Assert.NotNull(definition);
        var normalizedDefinition = definition.Replace("\"", string.Empty, StringComparison.Ordinal);
        Assert.StartsWith(
            expectedUnique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ",
            normalizedDefinition,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            $"({string.Join(", ", expectedColumns)})",
            normalizedDefinition,
            StringComparison.OrdinalIgnoreCase
        );
        if (expectedFilter is null)
            Assert.DoesNotContain(" WHERE ", normalizedDefinition, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(expectedFilter, normalizedDefinition, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DatabaseProvider provider,
        string tableName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = provider switch
        {
            DatabaseProvider.Sqlite => """
                SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName)
                """,
            DatabaseProvider.Postgres => """
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = current_schema() AND table_name = @tableName
                )
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        AddParameter(command, "tableName", tableName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        DatabaseProvider provider,
        string tableName,
        string columnName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = provider switch
        {
            DatabaseProvider.Sqlite => """
                SELECT EXISTS (
                    SELECT 1 FROM pragma_table_info(@tableName) WHERE name = @columnName
                )
                """,
            DatabaseProvider.Postgres => """
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema() AND table_name = @tableName AND column_name = @columnName
                )
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        AddParameter(command, "tableName", tableName);
        AddParameter(command, "columnName", columnName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(DatabaseProvider provider, string connectionString)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
        return options.Options;
    }

    private static string CreateDatabasePath() =>
        Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data")).FullName,
            $"jobs-event-timer-migration-{Guid.NewGuid():N}.db"
        );
}
