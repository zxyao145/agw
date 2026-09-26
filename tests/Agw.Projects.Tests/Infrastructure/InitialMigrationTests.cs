using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Tests;

public sealed class InitialMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_SqliteAndPostgres_CreatesCurrentSchema(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        var provider = usePostgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite;
        var connectionString = usePostgres
            ? "Host=localhost;Database=agw;Username=agw;Password=unused"
            : "Data Source=:memory:";
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
        using var dbContext = new AgwDbContext(options.Options);

        Assert.False(dbContext.Database.HasPendingModelChanges());

        var migration = Assert.Single(dbContext.Database.GetMigrations());
        Assert.EndsWith("_ReInit", migration, StringComparison.Ordinal);
        var migrator = dbContext.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            Migration.InitialDatabase,
            migration,
            MigrationsSqlGenerationOptions.NoTransactions
        );

        foreach (
            var expected in new[]
            {
                "integration_connection",
                "plugin_installation",
                "protected_value",
                "project_memory",
                "project_conversation",
                "project_conversation_chat_history",
                "project_conversation_binding",
                "project_conversation_turn",
                "durable_execution",
                "scope_backfilled",
                "generation",
                "ix_durable_execution_scope_backfilled_user_id_id",
                "ix_durable_execution_user_id_project_id",
                "execution_stream_entry",
                "turn_sequence",
                "last_event_sequence",
                "agentflow_checkpoint",
                "external_agent_kind",
                "additional_directories",
                "response_schema",
                "lease_expires_at",
                "history_scope",
                "api_token",
                "user_memory",
                "normalized_name",
                "secret_hash",
                "create_by",
                "create_time",
                "user_id",
                "tools",
                "max_context_window_tokens",
                "max_output_tokens",
                "ck_model_token_limits",
                "active_execution_id",
                "active_attempt_started_at",
                "ix_job_active_execution_id",
                "ix_integration_connection_create_by_alias",
                "ck_job_active_attempt",
                "setting",
                "ix_setting_key",
                "ix_setting_user_id_key",
                "user_id IS NULL",
                "user_id IS NOT NULL",
                "DEFAULT 256000",
                "DEFAULT 64000",
            }
        )
        {
            Assert.Contains(expected, script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server_auth_state", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent_file_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("building_blocks", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project_context", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project_task_record", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_blocks", script, StringComparison.OrdinalIgnoreCase);

        if (usePostgres)
        {
            Assert.Contains("metadata jsonb", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("uuid", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timestamp with time zone", script, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("\"metadata\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("jsonb", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_CreatesCurrentSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(
                connection,
                migrations => migrations.MigrationsAssembly(AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);

        await dbContext.Database.MigrateAsync(cancellationToken);

        var appliedMigration = Assert.Single(await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.EndsWith("_ReInit", appliedMigration, StringComparison.Ordinal);
        Assert.True(await ColumnIsNotNullAsync(connection, "project", "additional_directories", cancellationToken));
        Assert.Equal(
            "'[]'",
            await ColumnDefaultAsync(connection, "project", "additional_directories", cancellationToken)
        );
        Assert.True(await ColumnExistsAsync(connection, "agent", "response_schema", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agent", "external_agent_kind", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation_turn", cancellationToken));
        Assert.True(
            await ColumnExistsAsync(connection, "project_conversation_chat_history", "turn_id", cancellationToken)
        );
        Assert.True(
            await ColumnIsNotNullAsync(connection, "project_conversation_chat_history", "purpose", cancellationToken)
        );
        Assert.True(await ColumnExistsAsync(connection, "durable_execution", "lease_epoch", cancellationToken));
        Assert.False(await ColumnExistsAsync(connection, "execution_stream_entry", "sequence", cancellationToken));
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "execution_stream_entry",
                "ix_execution_stream_entry_turn_id_turn_sequence",
                ["turn_id", "turn_sequence"],
                cancellationToken
            )
        );
        Assert.True(await TableExistsAsync(connection, "setting", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "server_auth_state", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "durable_execution", "project_id", cancellationToken));
        Assert.True(
            await ColumnExistsAsync(connection, "durable_execution", "project_conversation_id", cancellationToken)
        );
        Assert.True(await ColumnExistsAsync(connection, "durable_execution", "scope_backfilled", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "integration_connection", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "plugin_installation", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_memory", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "project_conversation", "generation", cancellationToken));
        Assert.False(
            await ColumnExistsAsync(connection, "project_conversation", "session_generation", cancellationToken)
        );
        Assert.True(await TableExistsAsync(connection, "project_conversation_chat_history", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation_binding", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "task_session_binding", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "durable_execution", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "execution_stream_entry", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "agentflow_checkpoint", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "api_token", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "user_memory", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "create_by", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "create_time", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "secret_hash", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "durable_execution", "user_id", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agentflow_checkpoint", "user_id", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "job", "active_execution_id", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "job", "active_attempt_started_at", cancellationToken));
        Assert.True(await IndexIsUniqueAsync(connection, "job", "ix_job_active_execution_id", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agent", "tools", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agent", "enable", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agentflow", "enable", cancellationToken));
        Assert.True(await ColumnIsNotNullAsync(connection, "agent", "enable", cancellationToken));
        Assert.True(await ColumnIsNotNullAsync(connection, "agentflow", "enable", cancellationToken));
        Assert.Equal("1", await ColumnDefaultAsync(connection, "agent", "enable", cancellationToken));
        Assert.Equal("1", await ColumnDefaultAsync(connection, "agentflow", "enable", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "project", "tools", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "model", "max_context_window_tokens", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "model", "max_output_tokens", cancellationToken));
        Assert.False(await ColumnExistsAsync(connection, "model", "max_tokens", cancellationToken));
        Assert.Equal(
            "256000",
            await ColumnDefaultAsync(connection, "model", "max_context_window_tokens", cancellationToken)
        );
        Assert.Equal("64000", await ColumnDefaultAsync(connection, "model", "max_output_tokens", cancellationToken));
        Assert.Contains(
            "ck_model_token_limits",
            await TableSqlAsync(connection, "model", cancellationToken),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "ck_job_active_attempt",
            await TableSqlAsync(connection, "job", cancellationToken),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.False(await ColumnExistsAsync(connection, "agent", "building_blocks", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "agent_file_memory", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_context", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_task_record", cancellationToken));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($tableName) WHERE name = $columnName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$columnName", columnName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<string?> ColumnDefaultAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT dflt_value FROM pragma_table_info($tableName) WHERE name = $columnName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$columnName", columnName);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> TableSqlAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }

    private static async Task<bool> IndexIsUniqueAsync(
        SqliteConnection connection,
        string tableName,
        string indexName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [unique] FROM pragma_index_list($tableName) WHERE name = $indexName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$indexName", indexName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> IndexHasColumnsAsync(
        SqliteConnection connection,
        string tableName,
        string indexName,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_info($indexName) ORDER BY seqno;";
        command.Parameters.AddWithValue("$indexName", indexName);
        var actualColumns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actualColumns.Add(reader.GetString(0));
        }

        return actualColumns.SequenceEqual(expectedColumns, StringComparer.Ordinal)
            && await IndexIsUniqueAsync(connection, tableName, indexName, cancellationToken);
    }

    private static async Task<bool> ColumnIsNotNullAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [notnull] FROM pragma_table_info($tableName) WHERE name = $columnName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$columnName", columnName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
