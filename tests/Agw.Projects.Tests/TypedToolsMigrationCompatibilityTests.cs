using Agw.Infrastructure.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Projects.Tests;

public sealed class TypedToolsMigrationCompatibilityTests
{
    private const string PreviousMigration = "20260728145720_UpdateBlock";
    private const string TypedToolsMigration =
        "20260729142455_ReplaceBuildingBlocksWithTypedTools";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_ReplacesColumnsWithoutMigratingBuildingBlockData(
        bool usePostgres)
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
            TypedToolsMigration,
            MigrationsSqlGenerationOptions.NoTransactions);

        Assert.DoesNotContain("tool_blocks", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[]", script, StringComparison.Ordinal);
        if (usePostgres)
        {
            Assert.Contains("building_blocks", script, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(
                """ALTER TABLE "project" DROP COLUMN "building_blocks";""",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                """ALTER TABLE "agent" DROP COLUMN "building_blocks";""",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ef_temp_agent", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ef_temp_project", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRAGMA foreign_keys", script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("RENAME COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent_file_memory", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent_session_state", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migrate_SqliteDatabaseWithOrphanedAgentProvider_PreservesAgent()
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

        await ExecuteNonQueryAsync(
            connection,
            "PRAGMA foreign_keys = OFF;",
            cancellationToken);

        var agentId = Guid.CreateVersion7();
        var missingModelProviderId = Guid.CreateVersion7();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO "agent" (
                    "id",
                    "create_time",
                    "description",
                    "display_name",
                    "enable_summary",
                    "environment_variables",
                    "model_provider_id",
                    "name",
                    "system_prompt",
                    "tools",
                    "type")
                VALUES (
                    $id,
                    $createTime,
                    'description',
                    'Agent',
                    0,
                    '{}',
                    $modelProviderId,
                    'orphaned-provider-agent',
                    'prompt',
                    NULL,
                    0);
                """;
            command.Parameters.AddWithValue("$id", agentId);
            command.Parameters.AddWithValue(
                "$createTime",
                TimeProvider.System.GetUtcNow());
            command.Parameters.AddWithValue(
                "$modelProviderId",
                missingModelProviderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "PRAGMA foreign_keys = ON;",
            cancellationToken);
        await using (var foreignKeysCommand = connection.CreateCommand())
        {
            foreignKeysCommand.CommandText = "PRAGMA foreign_keys;";
            var foreignKeysEnabled =
                Convert.ToInt64(await foreignKeysCommand.ExecuteScalarAsync(cancellationToken));
            Assert.Equal(1, foreignKeysEnabled);
        }

        await migrator.MigrateAsync(TypedToolsMigration, cancellationToken);

        await using var verifyCommand = connection.CreateCommand();
        verifyCommand.CommandText =
            """SELECT "tools" FROM "agent" WHERE "id" = $id;""";
        verifyCommand.Parameters.AddWithValue("$id", agentId);
        var tools = await verifyCommand.ExecuteScalarAsync(cancellationToken);
        Assert.Equal("[]", tools);

        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM pragma_table_info('agent')
            WHERE name = 'building_blocks';
            """;
        var buildingBlockColumnCount =
            Convert.ToInt64(await columnCommand.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(0, buildingBlockColumnCount);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
