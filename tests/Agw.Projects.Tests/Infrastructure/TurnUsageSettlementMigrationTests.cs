using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// Turn 用量结算迁移：已经结束的 Turn 从结束时间起结算，未结束的 Turn 保持为空；SQLite 降级直接删除列，不重建表。
/// The turn usage settlement migration: finished turns settle at their finish time while unfinished turns stay null; the SQLite downgrade drops the columns without rebuilding tables.
/// </summary>
public sealed class TurnUsageSettlementMigrationTests
{
    private const string MigrationSuffix = "_AddTurnUsageSettlement";

    // 结束时间带有毫秒以下的部分。
    // The finish time carries digits below one millisecond.
    private static readonly DateTimeOffset FinishedAt = new DateTimeOffset(
        2026,
        10,
        23,
        8,
        0,
        5,
        123,
        TimeSpan.Zero
    ).AddTicks(4567);

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact]
    public async Task Upgrade_Sqlite_FinishedTurnsSettleAtFinishTime()
    {
        var path = Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data")).FullName,
            $"turn-usage-migration-{Guid.NewGuid():N}.db"
        );
        try
        {
            var settled = await UpgradeAsync(
                CreateOptions(
                    DatabaseProvider.Sqlite,
                    new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()
                )
            );

            // SQLite 以毫秒精度从结束时间换算结算时间。
            // SQLite derives the settlement time from the finish time at millisecond precision.
            Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 5, 123, TimeSpan.Zero), settled.Finished);
            Assert.Null(settled.Running);
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
    public async Task Upgrade_Postgres_FinishedTurnsSettleAtFinishTime()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        var database = $"agw_turn_usage_migration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(token);
        }
        try
        {
            var settled = await UpgradeAsync(
                CreateOptions(
                    DatabaseProvider.Postgres,
                    new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString
                )
            );

            Assert.NotNull(settled.Finished);
            Assert.Equal(settled.FinishedAt, settled.Finished);
            Assert.Null(settled.Running);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Downgrade_Sqlite_DropsColumnsWithoutRebuildingTables()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data")).FullName,
            $"turn-usage-migration-{Guid.NewGuid():N}.db"
        );
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            await using var context = new AgwDbContext(CreateOptions(DatabaseProvider.Sqlite, connectionString));
            var (previous, migration) = GetMigrations(context);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migration, token);

            var script = migrator.GenerateScript(migration, previous);
            await migrator.MigrateAsync(previous, token);

            Assert.DoesNotContain("ef_temp_", script, StringComparison.Ordinal);
            Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                await SqliteColumnExistsAsync(connectionString, "project_conversation_turn", "usage_settles_at")
            );
            Assert.False(
                await SqliteColumnExistsAsync(connectionString, "durable_execution", "interrupted_lease_epoch")
            );
            Assert.False(await SqliteColumnExistsAsync(connectionString, "agent_usage", "turn_id"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 在上一个迁移的表结构中写入一个已结束和一个运行中的 Turn，升级后读取已结束 Turn 保存的结束时间与两者的结算时间。
    /// Writes one finished and one running turn on the previous migration's schema, upgrades, and reads the stored finish time and both settlement times.
    /// </summary>
    private static async Task<(
        DateTimeOffset? FinishedAt,
        DateTimeOffset? Finished,
        DateTimeOffset? Running
    )> UpgradeAsync(DbContextOptions<AgwDbContext> options)
    {
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(options);
        var (previous, migration) = GetMigrations(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, token);
        var finished = Guid.CreateVersion7();
        var running = Guid.CreateVersion7();
        await InsertTurnAsync(context, finished, "completed", FinishedAt);
        await InsertTurnAsync(context, running, "running", null);

        await migrator.MigrateAsync(migration, token);

        var turns = await context
            .ProjectConversationTurns.IgnoreQueryFilters()
            .AsNoTracking()
            .ToDictionaryAsync(turn => turn.Id, token);
        return (turns[finished].FinishedAt, turns[finished].UsageSettlesAt, turns[running].UsageSettlesAt);
    }

    private static (string Previous, string Migration) GetMigrations(AgwDbContext context)
    {
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(
            migrations,
            migration => migration.EndsWith(MigrationSuffix, StringComparison.Ordinal)
        );
        Assert.True(index > 0);
        return (migrations[index - 1], migrations[index]);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(DatabaseProvider provider, string connectionString)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
        return options.Options;
    }

    private static Task InsertTurnAsync(AgwDbContext context, Guid turnId, string status, DateTimeOffset? finishedAt) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project_conversation_turn (id, project_conversation_id, target_id, runtime_type, status, input_message_id, first_sequence, step_count, started_at, finished_at) VALUES ({turnId}, {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {"agent"}, {status}, {Guid.Empty}, {0L}, {0}, {FinishedAt.AddMinutes(-1)}, {finishedAt})",
            TestContext.Current.CancellationToken
        );

    private static async Task<bool> SqliteColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) == 1;
    }
}
