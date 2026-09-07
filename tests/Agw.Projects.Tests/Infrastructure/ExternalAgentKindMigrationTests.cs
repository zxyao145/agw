using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Tests;

public sealed partial class InitialMigrationTests
{
    private const string ExternalAgentKindMigrationSuffix = "_AddExternalAgentKind";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_ExternalAgentKind_AddsAndBackfillsColumnWithoutForeignKeys(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(
            options,
            usePostgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            usePostgres ? "Host=localhost;Database=agw;Username=agw;Password=unused" : "Data Source=:memory:"
        );
        using var context = new AgwDbContext(options.Options);
        var migrations = context.Database.GetMigrations().ToArray();
        var migrationIndex = Array.FindIndex(
            migrations,
            name => name.EndsWith(ExternalAgentKindMigrationSuffix, StringComparison.Ordinal)
        );
        Assert.True(migrationIndex > 0);

        var script = context
            .GetService<IMigrator>()
            .GenerateScript(migrations[migrationIndex - 1], migrations[migrationIndex]);

        Assert.Contains("external_agent_kind", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN 'claudecode' THEN 1", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN 'codex' THEN 2", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN 'pi' THEN 3", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateAsync_Sqlite_ExternalAgentKind_BackfillsKnownNamesAndPreservesUnknownRows()
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
        await using var context = new AgwDbContext(options);
        var migrations = context.Database.GetMigrations().ToArray();
        var migrationIndex = Array.FindIndex(
            migrations,
            name => name.EndsWith(ExternalAgentKindMigrationSuffix, StringComparison.Ordinal)
        );
        Assert.True(migrationIndex > 0);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[migrationIndex - 1], cancellationToken);

        foreach (var name in new[] { "ClaudeCode", "CODEX", "pi", "custom-external" })
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO agent
                    (id, display_name, name, description, system_prompt, enable_summary, type, tools,
                     environment_variables, create_time, create_by)
                VALUES
                    ($id, $name, $name, '', '', 0, 1, '[]', '{}', $now, 'tester');
                """;
            insert.Parameters.AddWithValue("$id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$now", TimeProvider.System.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await migrator.MigrateAsync(migrations[migrationIndex], cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT name, external_agent_kind FROM agent ORDER BY name;";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var actual = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(reader.GetString(0), reader.GetInt64(1));
        }

        Assert.Equal(1, actual["ClaudeCode"]);
        Assert.Equal(2, actual["CODEX"]);
        Assert.Equal(3, actual["pi"]);
        Assert.Equal(0, actual["custom-external"]);
    }
}
