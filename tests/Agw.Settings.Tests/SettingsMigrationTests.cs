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
    public void MigrationScripts_CreateSettingsDirectly_WithoutIntermediateTable(bool postgres)
    {
        var builder = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(
            builder,
            postgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            postgres ? "Host=localhost;Database=unused" : "Data Source=:memory:"
        );
        using var context = new AgwDbContext(builder.Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(
            migrations,
            migration => migration.EndsWith("_AddSettings", StringComparison.Ordinal)
        );
        Assert.True(index > 0);
        var migrator = context.GetService<IMigrator>();
        var up = migrator.GenerateScript(migrations[index - 1], migrations[index]);
        var down = migrator.GenerateScript(migrations[index], migrations[index - 1]);
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
    public async Task SqliteMigration_CreatesAndRollsBackSettings_PreservesExistingTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(
                connection,
                migrations => migrations.MigrationsAssembly(AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(
            migrations,
            migration => migration.EndsWith("_AddSettings", StringComparison.Ordinal)
        );
        Assert.True(index > 0);
        await migrator.MigrateAsync(migrations[index - 1], ct);
        var tokenId = Guid.CreateVersion7();
        var createdAt = DateTimeOffset.Parse("2026-09-12T07:08:09+00:00");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO api_token (id,name,normalized_name,prefix,secret_hash,create_time,create_by) VALUES ({tokenId},'kept','KEPT','agw_prefix','existing-hash',{createdAt},'creator')",
            ct
        );

        await migrator.MigrateAsync(migrations[index], ct);
        Assert.Empty(await context.Settings.IgnoreQueryFilters().ToArrayAsync(ct));
        var token = await context.ApiTokens.AsNoTracking().IgnoreQueryFilters().SingleAsync(ct);
        Assert.Equal(tokenId, token.Id);
        Assert.Equal("existing-hash", token.SecretHash);
        Assert.Equal(createdAt, token.CreateTime);
        Assert.Equal("creator", token.CreateBy);

        await migrator.MigrateAsync(migrations[index - 1], ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('setting', 'server_auth_state')";
        Assert.Equal(0L, await command.ExecuteScalarAsync(ct));
        Assert.Equal(tokenId, (await context.ApiTokens.AsNoTracking().IgnoreQueryFilters().SingleAsync(ct)).Id);

        // A rolled-back empty settings migration can be applied again.
        await migrator.MigrateAsync(migrations[index], ct);
        Assert.Empty(await context.Settings.IgnoreQueryFilters().ToArrayAsync(ct));
    }
}
