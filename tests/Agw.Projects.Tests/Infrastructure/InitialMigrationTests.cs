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
        var provider = usePostgres
            ? DatabaseProvider.Postgres
            : DatabaseProvider.Sqlite;
        var connectionString = usePostgres
            ? "Host=localhost;Database=agw;Username=agw;Password=unused"
            : "Data Source=:memory:";
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
        using var dbContext = new AgwDbContext(options.Options);

        var migrations = dbContext.Database.GetMigrations().ToArray();
        Assert.Equal(4, migrations.Length);
        Assert.EndsWith("_Init", migrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_AddApiTokenTable", migrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_AddUserMemory", migrations[2], StringComparison.Ordinal);
        Assert.EndsWith("_AddAgentflowCheckpoints", migrations[3], StringComparison.Ordinal);

        var script = dbContext.GetService<IMigrator>().GenerateScript(
            Migration.InitialDatabase,
            migrations[^1],
            MigrationsSqlGenerationOptions.NoTransactions);

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
        Assert.Contains("tools", script, StringComparison.OrdinalIgnoreCase);
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
                migrations => migrations.MigrationsAssembly(
                    AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);

        await dbContext.Database.MigrateAsync(cancellationToken);

        var appliedMigrations = (await dbContext.Database
                .GetAppliedMigrationsAsync(cancellationToken))
            .ToArray();
        Assert.Equal(4, appliedMigrations.Length);
        Assert.EndsWith("_Init", appliedMigrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_AddApiTokenTable", appliedMigrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_AddUserMemory", appliedMigrations[2], StringComparison.Ordinal);
        Assert.EndsWith("_AddAgentflowCheckpoints", appliedMigrations[3], StringComparison.Ordinal);
        Assert.True(await TableExistsAsync(connection, "integration_connection", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "plugin_installation", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_memory", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation", cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "project_conversation_chat_history",
            cancellationToken));
        Assert.True(await TableExistsAsync(connection, "durable_execution", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "execution_stream_entry", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "agentflow_checkpoint", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "api_token", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "user_memory", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "create_by", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "create_time", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "api_token", "secret_hash", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "agent", "tools", cancellationToken));
        Assert.True(await ColumnExistsAsync(connection, "project", "tools", cancellationToken));
        Assert.False(await ColumnExistsAsync(
            connection,
            "agent",
            "building_blocks",
            cancellationToken));
        Assert.False(await TableExistsAsync(connection, "agent_file_memory", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_context", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_task_record", cancellationToken));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info($tableName) WHERE name = $columnName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$columnName", columnName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
