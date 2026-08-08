using Agw.Infrastructure.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Projects.Tests;

public sealed class ProjectMemoryMigrationCompatibilityTests
{
    private const string PreviousMigration =
        "20260729142455_ReplaceBuildingBlocksWithTypedTools";
    private const string ProjectMemoryMigration =
        "20260807151950_ReplaceFileMemoryWithProjectMemory";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_ReplacesMemoryTableAndToolDiscriminator(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        if (usePostgres)
        {
            options.UseNpgsql(
                "Host=localhost;Database=agw;Username=agw;Password=unused");
        }
        else
        {
            options.UseSqlite("Data Source=:memory:");
        }

        options.UseSnakeCaseNamingConvention();
        using var dbContext = new AgwDbContext(options.Options);

        var script = dbContext.GetService<IMigrator>().GenerateScript(
            PreviousMigration,
            ProjectMemoryMigration,
            MigrationsSqlGenerationOptions.NoTransactions);

        Assert.Contains("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent_file_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file-memory", script, StringComparison.Ordinal);
        Assert.Contains("project-memory", script, StringComparison.Ordinal);
        Assert.Contains("ix_project_memory_project_id_path", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_SqliteRewritesToolsAndDiscardsLegacyMemoryInBothDirections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        const string legacyTools =
            """[{"kind":"toolBlock","definition":{"name":"file-memory","options":{"storage":"database"}}}]""";
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO "project" (
                "id", "create_time", "environment_variables", "name", "tools", "type")
            VALUES ($projectId, $createdAt, '{}', 'memory-project', $tools, 0);
            """,
            cancellationToken,
            ("$projectId", projectId),
            ("$createdAt", TimeProvider.System.GetUtcNow()),
            ("$tools", legacyTools));
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO "agent" (
                "id", "create_time", "description", "display_name", "enable_summary",
                "environment_variables", "name", "system_prompt", "tools", "type")
            VALUES (
                $agentId, $createdAt, '', 'Memory Agent', 0,
                '{}', 'memory-agent', '', $tools, 0);
            """,
            cancellationToken,
            ("$agentId", agentId),
            ("$createdAt", TimeProvider.System.GetUtcNow()),
            ("$tools", legacyTools));
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO "agent_file_memory" (
                "id", "agent_id", "content", "conversation_id",
                "path", "project_id", "updated_at")
            VALUES (
                $id, $agentId, 'legacy', $conversationId,
                'notes.md', $projectId, $updatedAt);
            """,
            cancellationToken,
            ("$id", Guid.CreateVersion7()),
            ("$agentId", agentId),
            ("$conversationId", conversationId),
            ("$projectId", projectId),
            ("$updatedAt", TimeProvider.System.GetUtcNow()));

        await migrator.MigrateAsync(ProjectMemoryMigration, cancellationToken);

        Assert.Equal(
            legacyTools.Replace("file-memory", "project-memory", StringComparison.Ordinal),
            await GetToolsAsync(connection, "project", projectId, cancellationToken));
        Assert.Equal(
            legacyTools.Replace("file-memory", "project-memory", StringComparison.Ordinal),
            await GetToolsAsync(connection, "agent", agentId, cancellationToken));
        Assert.False(await TableExistsAsync(connection, "agent_file_memory", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_memory", cancellationToken));
        Assert.Equal(
            0L,
            await ExecuteScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"project_memory\";",
                cancellationToken));

        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO "project_memory" (
                "id", "content", "path", "project_id", "updated_at")
            VALUES ($id, 'new', 'notes.md', $projectId, $updatedAt);
            """,
            cancellationToken,
            ("$id", Guid.CreateVersion7()),
            ("$projectId", projectId),
            ("$updatedAt", TimeProvider.System.GetUtcNow()));

        await migrator.MigrateAsync(PreviousMigration, cancellationToken);

        Assert.Equal(legacyTools, await GetToolsAsync(
            connection,
            "project",
            projectId,
            cancellationToken));
        Assert.Equal(legacyTools, await GetToolsAsync(
            connection,
            "agent",
            agentId,
            cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_memory", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "agent_file_memory", cancellationToken));
        Assert.Equal(
            0L,
            await ExecuteScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"agent_file_memory\";",
                cancellationToken));
    }

    private static Task<string?> GetToolsAsync(
        SqliteConnection connection,
        string table,
        Guid id,
        CancellationToken cancellationToken) =>
        ExecuteScalarAsync<string?>(
            connection,
            $"SELECT \"tools\" FROM \"{table}\" WHERE \"id\" = $id;",
            cancellationToken,
            ("$id", id));

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken) =>
        await ExecuteScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM "sqlite_master"
            WHERE "type" = 'table' AND "name" = $table;
            """,
            cancellationToken,
            ("$table", table)) == 1;

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
