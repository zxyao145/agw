using Agw.Infrastructure.Data;
using Agw.Shared;
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

        var migrations = dbContext.Database.GetMigrations().ToArray();
        Assert.Equal(9, migrations.Length);
        Assert.EndsWith("_Init", migrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_AddApiTokenTable", migrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_AddUserMemory", migrations[2], StringComparison.Ordinal);
        Assert.EndsWith("_AddAgentflowCheckpoints", migrations[3], StringComparison.Ordinal);
        Assert.EndsWith("_AddModelCompactionLimits", migrations[4], StringComparison.Ordinal);
        Assert.EndsWith("_UseUserIdForExecutionOwnership", migrations[5], StringComparison.Ordinal);
        Assert.EndsWith("_AddJobActiveAttempt", migrations[6], StringComparison.Ordinal);
        Assert.EndsWith("_EnforceUserOwnedConnections", migrations[7], StringComparison.Ordinal);
        Assert.EndsWith("_EnforceUserDataIsolation", migrations[8], StringComparison.Ordinal);

        var script = dbContext
            .GetService<IMigrator>()
            .GenerateScript(Migration.InitialDatabase, migrations[^1], MigrationsSqlGenerationOptions.NoTransactions);

        Assert.Contains("integration_connection", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugin_installation", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protected_value", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_conversation", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_conversation_chat_history", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable_execution", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution_stream_entry", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agentflow_checkpoint", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api_token", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("normalized_name", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret_hash", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create_by", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create_time", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE durable_execution SET user_id = '1001'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE agentflow_checkpoint SET user_id = '1001'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "UPDATE integration_connection SET create_by = '1001' WHERE create_by IS NULL",
            script,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("tools", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_context_window_tokens", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_output_tokens", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ck_model_token_limits", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active_execution_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active_attempt_started_at", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_job_active_execution_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_integration_connection_create_by_alias", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ck_job_active_attempt", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT 256000", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT 64000", script, StringComparison.OrdinalIgnoreCase);
        if (usePostgres)
        {
            Assert.Contains("metadata jsonb", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("uuid", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timestamp with time zone", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ALTER COLUMN create_by SET NOT NULL", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "ALTER TABLE job ALTER COLUMN create_by SET NOT NULL",
                script,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain(
                "ALTER COLUMN create_by TYPE character varying",
                script,
                StringComparison.OrdinalIgnoreCase
            );
        }
        else
        {
            Assert.Contains("\"metadata\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("jsonb", script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("agent_file_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("building_blocks", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project_context", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project_task_record", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_blocks", script, StringComparison.OrdinalIgnoreCase);
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

        var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        Assert.Equal(9, appliedMigrations.Length);
        Assert.EndsWith("_Init", appliedMigrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_AddApiTokenTable", appliedMigrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_AddUserMemory", appliedMigrations[2], StringComparison.Ordinal);
        Assert.EndsWith("_AddAgentflowCheckpoints", appliedMigrations[3], StringComparison.Ordinal);
        Assert.EndsWith("_AddModelCompactionLimits", appliedMigrations[4], StringComparison.Ordinal);
        Assert.EndsWith("_UseUserIdForExecutionOwnership", appliedMigrations[5], StringComparison.Ordinal);
        Assert.EndsWith("_AddJobActiveAttempt", appliedMigrations[6], StringComparison.Ordinal);
        Assert.EndsWith("_EnforceUserOwnedConnections", appliedMigrations[7], StringComparison.Ordinal);
        Assert.EndsWith("_EnforceUserDataIsolation", appliedMigrations[8], StringComparison.Ordinal);
        Assert.True(await TableExistsAsync(connection, "integration_connection", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "plugin_installation", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_memory", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation_chat_history", cancellationToken));
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

    [Fact]
    public async Task MigrateAsync_Sqlite_ModelCompactionLimitsPreserveExistingMaxTokens()
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
        var migrations = dbContext.Database.GetMigrations().ToArray();
        var migrator = dbContext.GetService<IMigrator>();
        var compactionMigration = migrations.Single(migration =>
            migration.EndsWith("_AddModelCompactionLimits", StringComparison.Ordinal)
        );
        var compactionIndex = Array.IndexOf(migrations, compactionMigration);
        await migrator.MigrateAsync(migrations[compactionIndex - 1], cancellationToken);
        var modelId = Guid.CreateVersion7();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO model (id, name, max_tokens, create_time) "
                + "VALUES ($id, $name, $maxTokens, $createTime);";
            insert.Parameters.AddWithValue("$id", modelId);
            insert.Parameters.AddWithValue("$name", "existing-model");
            insert.Parameters.AddWithValue("$maxTokens", 128_000);
            insert.Parameters.AddWithValue("$createTime", TimeProvider.System.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await migrator.MigrateAsync(compactionMigration, cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT max_context_window_tokens, max_output_tokens FROM model WHERE id = $id;";
        select.Parameters.AddWithValue("$id", modelId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(128_000, reader.GetInt32(0));
        Assert.Equal(64_000, reader.GetInt32(1));
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_ExecutionOwnershipBackfillsAdminUserId()
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
        var migrations = dbContext.Database.GetMigrations().ToArray();
        var ownershipMigration = migrations.Single(migration =>
            migration.EndsWith("_UseUserIdForExecutionOwnership", StringComparison.Ordinal)
        );
        var ownershipIndex = Array.IndexOf(migrations, ownershipMigration);
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[ownershipIndex - 1], cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO project
                    (id, name, type, tools, environment_variables, create_time)
                VALUES
                    ($projectId, 'Migration test', 0, '[]', '{}', $now);

                INSERT INTO project_conversation
                    (id, project_id, context_id, title, create_time)
                VALUES
                    ($conversationId, $projectId, 'context-1', 'Migration test', $now);

                INSERT INTO durable_execution
                    (id, user_name, manifest_json, status, segment_index, state_changed_at, state_version, create_time)
                VALUES
                    ($executionId, 'admin', '{}', 0, 0, $now, $stateVersion, $now);

                INSERT INTO agentflow_checkpoint
                    (id, project_id, project_conversation_id, context_id, task_id, agentflow_id, user_name,
                     is_durable, boundary_sequence, definition_fingerprint, markers_json, checkpoint_json, create_time)
                VALUES
                    ($checkpointId, $projectId, $conversationId, 'context-1', $taskId, $agentflowId, 'admin',
                     0, 0, $fingerprint, '[]', '{}', $now);
                """;
            insert.Parameters.AddWithValue("$executionId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$stateVersion", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$checkpointId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$projectId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$conversationId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$taskId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$agentflowId", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            insert.Parameters.AddWithValue("$now", TimeProvider.System.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await migrator.MigrateAsync(ownershipMigration, cancellationToken);

        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                "SELECT "
                + "(SELECT user_id FROM durable_execution LIMIT 1), "
                + "(SELECT user_id FROM agentflow_checkpoint LIMIT 1);";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(Constants.AdminUserId, reader.GetString(0));
            Assert.Equal(Constants.AdminUserId, reader.GetString(1));
        }

        await migrator.MigrateAsync(migrations[ownershipIndex - 1], cancellationToken);

        Assert.True(await ColumnExistsAsync(connection, "durable_execution", "user_name", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agentflow_checkpoint", "user_name", cancellationToken));
        Assert.False(await ColumnExistsAsync(connection, "durable_execution", "user_id", cancellationToken));
        Assert.False(await ColumnExistsAsync(connection, "agentflow_checkpoint", "user_id", cancellationToken));
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_ConnectionOwnershipBackfillsAdminUserId()
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
        var migrations = dbContext.Database.GetMigrations().ToArray();
        var ownershipMigration = migrations.Single(migration =>
            migration.EndsWith("_EnforceUserOwnedConnections", StringComparison.Ordinal)
        );
        var ownershipIndex = Array.IndexOf(migrations, ownershipMigration);
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[ownershipIndex - 1], cancellationToken);
        var connectionId = Guid.CreateVersion7();

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO integration_connection
                    (id, plugin_id, connector_id, auth_scheme_id, display_name, alias,
                     configuration_json, enabled, status, create_time, create_by)
                VALUES
                    ($id, 'github', 'github-cloud', 'oauth2', 'Legacy GitHub', 'legacy-github',
                     '{}', 1, 'Unverified', $now, NULL);
                """;
            insert.Parameters.AddWithValue("$id", connectionId);
            insert.Parameters.AddWithValue("$now", TimeProvider.System.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await migrator.MigrateAsync(ownershipMigration, cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT create_by FROM integration_connection WHERE id = $id;";
        select.Parameters.AddWithValue("$id", connectionId);
        Assert.Equal(Constants.AdminUserId, Convert.ToString(await select.ExecuteScalarAsync(cancellationToken)));
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_UserIsolationUsesCompositeIndexes()
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

        var migrations = dbContext.Database.GetMigrations().ToArray();
        var userIsolationScript = dbContext
            .GetService<IMigrator>()
            .GenerateScript(migrations[^2], migrations[^1], MigrationsSqlGenerationOptions.NoTransactions);

        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "project",
                "ix_project_create_by_name",
                ["create_by", "name"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "agent",
                "ix_agent_create_by_name",
                ["create_by", "name"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "skill",
                "ix_skill_create_by_name",
                ["create_by", "name"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "model",
                "ix_model_create_by_name",
                ["create_by", "name"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "api_token",
                "ix_api_token_create_by_normalized_name",
                ["create_by", "normalized_name"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "plugin_installation",
                "ix_plugin_installation_create_by_plugin_id",
                ["create_by", "plugin_id"],
                cancellationToken
            )
        );
        Assert.True(
            await IndexHasColumnsAsync(
                connection,
                "provider",
                "ix_provider_create_by_name_provider_type",
                ["create_by", "name", "provider_type"],
                cancellationToken
            )
        );
        Assert.True(await ColumnIsNotNullAsync(connection, "plugin_installation", "create_by", cancellationToken));
        Assert.True(await ColumnIsNotNullAsync(connection, "job", "create_by", cancellationToken));
        Assert.True(await ColumnIsNotNullAsync(connection, "agent_usage", "user_id", cancellationToken));

        Assert.Contains("SELECT create_by FROM project", userIsolationScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOREIGN KEY", userIsolationScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_UserIsolationBackfillsAgentUsageFromProjectOwner()
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
        var migrations = dbContext.Database.GetMigrations().ToArray();
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[^2], cancellationToken);

        var projectId = Guid.CreateVersion7();
        var usageId = Guid.CreateVersion7();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO project
                    (id, name, type, tools, environment_variables, create_time, create_by)
                VALUES
                    ($projectId, 'Owned project', 0, '[]', '{}', $now, 'user-42');

                INSERT INTO agent_usage
                    (id, project_id, context_id, agent_name, recorded_at,
                     input_token_count, output_token_count, total_token_count,
                     cached_input_token_count, reasoning_token_count)
                VALUES
                    ($usageId, $projectId, 'context-1', 'agent', $now, 1, 2, 3, 0, 0);
                """;
            insert.Parameters.AddWithValue("$projectId", projectId);
            insert.Parameters.AddWithValue("$usageId", usageId);
            insert.Parameters.AddWithValue("$now", TimeProvider.System.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await migrator.MigrateAsync(migrations[^1], cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT user_id FROM agent_usage WHERE id = $usageId;";
        select.Parameters.AddWithValue("$usageId", usageId);
        Assert.Equal("user-42", Convert.ToString(await select.ExecuteScalarAsync(cancellationToken)));
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
