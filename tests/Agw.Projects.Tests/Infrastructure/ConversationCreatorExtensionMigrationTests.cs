using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 对话创建方迁移：已有对话的创建方为 host，列不可为空并以 host 为默认值；SQLite 降级直接删除列，不重建表。
/// The conversation creator migration: existing conversations get the host creator, the column is required with a host default, and the SQLite downgrade drops the column without rebuilding tables.
/// </summary>
public sealed class ConversationCreatorExtensionMigrationTests
{
    private const string MigrationSuffix = "_AddConversationCreatorExtension";

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact]
    public async Task Upgrade_Sqlite_ExistingConversationsGetHostCreator()
    {
        var path = CreateDatabasePath();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            var creator = await UpgradeAsync(CreateOptions(DatabaseProvider.Sqlite, connectionString));

            Assert.Equal(ConversationCreators.Host, creator);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"notnull\", dflt_value FROM pragma_table_info('project_conversation') WHERE name = 'extension_id'";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("'host'", reader.GetString(1));
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
    public async Task Upgrade_Postgres_AddsRequiredColumnWithHostDefault()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        var database = $"agw_conversation_creator_migration_{Guid.NewGuid():N}";
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
            var creator = await UpgradeAsync(CreateOptions(DatabaseProvider.Postgres, connectionString));

            Assert.Equal(ConversationCreators.Host, creator);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand(
                "SELECT is_nullable, column_default, character_maximum_length FROM information_schema.columns WHERE table_name = 'project_conversation' AND column_name = 'extension_id'",
                connection
            );
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal("NO", reader.GetString(0));
            Assert.StartsWith("'host'", reader.GetString(1), StringComparison.Ordinal);
            Assert.Equal(64, reader.GetInt32(2));
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
    public async Task Downgrade_Sqlite_DropsColumnWithoutRebuildingTables()
    {
        var token = TestContext.Current.CancellationToken;
        var path = CreateDatabasePath();
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
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('project_conversation') WHERE name = 'extension_id'";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(token)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 在上一个迁移的表结构中写入一个对话，升级后读取它的创建方。
    /// Writes a conversation on the previous migration's schema, upgrades, and reads its creator.
    /// </summary>
    private static async Task<string> UpgradeAsync(DbContextOptions<AgwDbContext> options)
    {
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(options);
        var (previous, migration) = GetMigrations(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, token);
        var conversationId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project_conversation (id, generation, project_id, context_id, title, create_by, create_time) VALUES ({conversationId}, {0}, {Guid.CreateVersion7()}, {"existing-context"}, {"Existing"}, {"1001"}, {new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero)})",
            token
        );

        await migrator.MigrateAsync(migration, token);

        return await context
            .ProjectConversations.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.ExtensionId)
            .SingleAsync(token);
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

    private static string CreateDatabasePath() =>
        Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data")).FullName,
            $"conversation-creator-migration-{Guid.NewGuid():N}.db"
        );
}
