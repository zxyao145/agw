using System.Data.Common;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agw.Infrastructure.Tests;

public sealed class EventSourceSimplificationMigrationTests
{
    private const string PreviousMigrationName = "MoveJobsToEventTimers";
    private const string MigrationName = "SimplifyEventSourcesAndSubscriptions";

    public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(IsolatedTestConnections.Postgres);

    [Fact]
    public async Task Upgrade_Sqlite_ClearsEventDataAndCreatesNameIndex()
    {
        var path = Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data")).FullName,
            $"event-source-migration-{Guid.CreateVersion7():N}.db"
        );
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            await UpgradeAsync(DatabaseProvider.Sqlite, connectionString);
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
    public async Task Upgrade_Postgres_ClearsEventDataAndCreatesNameIndex()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = new NpgsqlConnectionStringBuilder(IsolatedTestConnections.Postgres!) { Pooling = false };
        var database = $"agw_event_source_migration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(token);
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = database,
            }.ConnectionString;
            await UpgradeAsync(DatabaseProvider.Postgres, connectionString);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task UpgradeAsync(DatabaseProvider provider, string connectionString)
    {
        var token = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
        await using var context = new AgwDbContext(options.Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationName, token);
        var oldSourceId = Guid.CreateVersion7();
        var otherSourceId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        await InsertSourceAsync(context, oldSourceId, "same", now, token);
        await InsertSourceAsync(context, otherSourceId, "same", now, token);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO event_subscription
                (id, name, enabled, source_ids_json, event_types_json, target_kind, version, create_time, create_by)
            VALUES ({Guid.CreateVersion7()}, {"old"}, {true}, {"[]"}, {"[]"}, {0}, {0L}, {now}, {"owner"})
            """,
            token
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO event
                (id, source_id, ce_spec_version, ce_source, ce_id, type, ce_data_is_binary,
                 received_at, chain_id, hops, dispatch_state, matched_count, recover_after, create_time, create_by)
            VALUES ({Guid.CreateVersion7()}, {oldSourceId}, {"1.0"}, {"/upstream"}, {"old"}, {"demo.old"},
                    {false}, {now}, {Guid.CreateVersion7()}, {0}, {0}, {0}, {now}, {now}, {"owner"})
            """,
            token
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO event_source_item
                (id, source_id, item_key, fingerprint, change_seq, first_seen_at, last_seen_at)
            VALUES ({Guid.CreateVersion7()}, {oldSourceId}, {"old"}, {""}, {0L}, {now}, {now})
            """,
            token
        );

        var migration = Assert.Single(
            context.Database.GetMigrations(),
            id => id.EndsWith($"_{MigrationName}", StringComparison.Ordinal)
        );
        await migrator.MigrateAsync(migration, token);
        await context.Database.OpenConnectionAsync(token);
        var connection = context.Database.GetDbConnection();
        foreach (
            var table in new[]
            {
                "event_delivery_attempt",
                "event_delivery",
                "event_outbox",
                "event",
                "event_schedule",
                "event_source_item",
                "event_subscription",
                "event_source",
            }
        )
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(token)));
        }
        await context.Database.CloseConnectionAsync();

        var currentId = Guid.CreateVersion7();
        await InsertSourceAsync(context, currentId, "webhook.same", now, token);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertSourceAsync(context, Guid.CreateVersion7(), "webhook.same", now, token)
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE event_source SET deleted_at = {now} WHERE id = {currentId}",
            token
        );
        await InsertSourceAsync(context, Guid.CreateVersion7(), "webhook.same", now, token);
    }

    private static Task<int> InsertSourceAsync(
        AgwDbContext context,
        Guid id,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO event_source
                (id, name, kind, ce_source, config_json, enabled, version, create_time, create_by)
            VALUES ({id}, {name}, {0}, {"/upstream"}, {"{}"}, {true}, {0L}, {now}, {"owner"})
            """,
            cancellationToken
        );
}
