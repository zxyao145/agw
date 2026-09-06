using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Tests;

public sealed partial class InitialMigrationTests
{
    private const string BindingRenameMigrationSuffix = "_RenameTaskSessionBindingToProjectConversationBinding";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_BindingRename_RenamesTableWithoutForeignKeys(bool usePostgres)
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(
            options,
            usePostgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            usePostgres ? "Host=localhost;Database=agw;Username=agw;Password=unused" : "Data Source=:memory:"
        );
        using var context = new AgwDbContext(options.Options);
        var migrations = context.Database.GetMigrations().ToArray();
        var renameIndex = Array.FindIndex(
            migrations,
            name => name.EndsWith(BindingRenameMigrationSuffix, StringComparison.Ordinal)
        );
        Assert.True(renameIndex > 0);
        var migrator = context.GetService<IMigrator>();

        // Act
        var up = migrator.GenerateScript(migrations[renameIndex - 1], migrations[renameIndex]).Replace("\"", "");
        var down = migrator.GenerateScript(migrations[renameIndex], migrations[renameIndex - 1]).Replace("\"", "");

        // Assert
        Assert.Contains("ALTER TABLE task_session_binding RENAME TO project_conversation_binding", up);
        Assert.Contains("ALTER TABLE project_conversation_binding RENAME TO task_session_binding", down);
        Assert.DoesNotContain("FOREIGN KEY", up, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOREIGN KEY", down, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateAsync_BindingRename_PreservesRowsAndUniqueIndexOnUpgradeAndRollback()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(
                connection,
                builder => builder.MigrationsAssembly(AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToArray();
        var renameIndex = Array.FindIndex(
            migrations,
            name => name.EndsWith(BindingRenameMigrationSuffix, StringComparison.Ordinal)
        );
        Assert.True(renameIndex > 0);
        await migrator.MigrateAsync(migrations[renameIndex - 1], token);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO task_session_binding (id,project_conversation_id,agent_id,external_agent_name,provider_session_id,create_by,create_time) VALUES ({Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()},'codex','existing-thread-id','tester',{TimeProvider.System.GetUtcNow()})",
            token
        );
        var originalRow = await ReadBindingRowAsync(connection, "task_session_binding", token);
        Assert.NotNull(originalRow);

        foreach (
            var (migration, table, oldTable) in new[]
            {
                (migrations[renameIndex], "project_conversation_binding", "task_session_binding"),
                (migrations[renameIndex - 1], "task_session_binding", "project_conversation_binding"),
            }
        )
        {
            // Act
            await migrator.MigrateAsync(migration, token);

            // Assert
            Assert.True(await TableExistsAsync(connection, table, token));
            Assert.False(await TableExistsAsync(connection, oldTable, token));
            Assert.Equal(originalRow, await ReadBindingRowAsync(connection, table, token));
            Assert.DoesNotContain(
                "FOREIGN KEY",
                await TableSqlAsync(connection, table, token),
                StringComparison.OrdinalIgnoreCase
            );
            Assert.True(
                await IndexIsUniqueAsync(
                    connection,
                    table,
                    $"ix_{table}_project_conversation_id_agent_id_external_agent_name",
                    token
                )
            );
            await using var duplicate = connection.CreateCommand();
            duplicate.CommandText = $"""
                INSERT INTO {table} (id,project_conversation_id,agent_id,external_agent_name,provider_session_id,create_by,create_time)
                SELECT $id,project_conversation_id,agent_id,external_agent_name,provider_session_id,create_by,create_time FROM {table};
                """;
            duplicate.Parameters.AddWithValue("$id", Guid.NewGuid());
            var exception = await Assert.ThrowsAsync<SqliteException>(() => duplicate.ExecuteNonQueryAsync(token));
            Assert.Equal(19, exception.SqliteErrorCode);
        }
    }

    private static async Task<object?> ReadBindingRowAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id || '|' || project_conversation_id || '|' || agent_id || '|' || external_agent_name || '|' || provider_session_id || '|' || create_by || '|' || create_time
            FROM {table};
            """;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
