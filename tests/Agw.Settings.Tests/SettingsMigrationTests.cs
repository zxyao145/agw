using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Agw.Settings.Tests;

public sealed class SettingsMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialMigrationScripts_CreateSettingsWithoutForeignKeys(bool postgres)
    {
        var builder = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(
            builder,
            postgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            postgres ? "Host=localhost;Database=unused" : "Data Source=:memory:"
        );
        using var context = new AgwDbContext(builder.Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = Assert.Single(context.Database.GetMigrations());
        Assert.EndsWith("_ReInit", migration, StringComparison.Ordinal);

        var migrator = context.GetService<IMigrator>();
        var up = migrator.GenerateScript(Migration.InitialDatabase, migration);
        var down = migrator.GenerateScript(migration, Migration.InitialDatabase);

        Assert.Contains("CREATE TABLE", up, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_setting_key", up);
        Assert.Contains("ix_setting_user_id_key", up);
        Assert.Contains("user_id IS NULL", up);
        Assert.Contains("user_id IS NOT NULL", up);
        Assert.Contains("DROP TABLE", down, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", down, StringComparison.OrdinalIgnoreCase);
        foreach (var script in new[] { up, down })
        {
            Assert.DoesNotContain("server_auth_state", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SqliteInitialMigration_CreatesAndRollsBackSettings()
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
        var migration = Assert.Single(context.Database.GetMigrations());
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(null, cancellationToken);
        Assert.Empty(await context.Settings.IgnoreQueryFilters().ToArrayAsync(cancellationToken));

        await migrator.MigrateAsync(Migration.InitialDatabase, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('setting', 'server_auth_state')";
        Assert.Equal(0L, await command.ExecuteScalarAsync(cancellationToken));

        await migrator.MigrateAsync(migration, cancellationToken);
        Assert.Empty(await context.Settings.IgnoreQueryFilters().ToArrayAsync(cancellationToken));
    }
}
