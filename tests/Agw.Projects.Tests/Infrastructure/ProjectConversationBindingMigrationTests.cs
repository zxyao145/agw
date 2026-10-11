using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 绑定历史迁移的升级与降级：升级后已有记录全部生效；同组只有一条记录时降级恢复原表结构，同组多条记录时降级终止并保留当前表结构与数据。
/// Upgrade and downgrade of the binding history migration: existing records are all active after the upgrade; a downgrade restores the original schema when every group has one record, and stops while keeping the current schema and data when a group has several records.
/// </summary>
public sealed class ProjectConversationBindingMigrationTests
{
    private const string MigrationSuffix = "_AddProjectConversationBindingHistory";

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Theory]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.Postgres)]
    public void GenerateDowngradeScript_RestoresOriginalIndexBeforeRemovingHistorySchema(DatabaseProvider provider)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(
            options,
            provider,
            provider == DatabaseProvider.Postgres ? "Host=localhost;Database=unused" : "Data Source=:memory:"
        );
        using var context = new AgwDbContext(options.Options);
        var (previous, latest) = GetMigrations(context);

        var script = context.GetService<IMigrator>().GenerateScript(latest, previous);

        var restore = script.IndexOf(OriginalIndexName(provider), StringComparison.Ordinal);
        Assert.True(restore >= 0);
        Assert.Contains("CREATE UNIQUE INDEX", script[..restore], StringComparison.Ordinal);
        Assert.True(restore < script.IndexOf("DROP INDEX", StringComparison.Ordinal));
        Assert.True(restore < script.IndexOf("DROP COLUMN", StringComparison.Ordinal));
        Assert.DoesNotContain("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ef_temp_", script, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(script, provider == DatabaseProvider.Postgres ? "START TRANSACTION" : "BEGIN TRANSACTION")
        );
    }

    [Fact]
    public async Task Upgrade_Sqlite_ExistingBindingsBecomeActive()
    {
        await using var database = await SqliteMigrationDatabase.CreateAsync();
        await Upgrade_ExistingBindingsBecomeActive(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_SqliteSingleRecordPerGroup_RestoresOriginalSchema(bool useScript)
    {
        await using var database = await SqliteMigrationDatabase.CreateAsync();
        await Downgrade_SingleRecordPerGroup_RestoresOriginalSchema(database, useScript);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_SqliteMultipleRecordsInGroup_FailsAndKeepsCurrentSchema(bool useScript)
    {
        await using var database = await SqliteMigrationDatabase.CreateAsync();
        await Downgrade_MultipleRecordsInGroup_FailsAndKeepsCurrentSchema(database, useScript);
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    public async Task Upgrade_Postgres_ExistingBindingsBecomeActive()
    {
        await using var database = await PostgresMigrationDatabase.CreateAsync();
        await Upgrade_ExistingBindingsBecomeActive(database);
    }

    [Theory(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_PostgresSingleRecordPerGroup_RestoresOriginalSchema(bool useScript)
    {
        await using var database = await PostgresMigrationDatabase.CreateAsync();
        await Downgrade_SingleRecordPerGroup_RestoresOriginalSchema(database, useScript);
    }

    [Theory(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_PostgresMultipleRecordsInGroup_FailsAndKeepsCurrentSchema(bool useScript)
    {
        await using var database = await PostgresMigrationDatabase.CreateAsync();
        await Downgrade_MultipleRecordsInGroup_FailsAndKeepsCurrentSchema(database, useScript);
    }

    private static async Task Upgrade_ExistingBindingsBecomeActive(MigrationDatabase database)
    {
        var token = TestContext.Current.CancellationToken;
        await using var context = database.CreateContext();
        var (previous, latest) = GetMigrations(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, token);
        var conversationId = Guid.CreateVersion7();
        await InsertLegacyBindingAsync(context, conversationId, "codex", "legacy-codex");
        await InsertLegacyBindingAsync(context, conversationId, "pi", "legacy-pi");

        await migrator.MigrateAsync(latest, token);

        Assert.Equal([true, true], await ReadActiveFlagsAsync(context));
        Assert.Equal(
            ["ux_project_conversation_binding_active", "ux_project_conversation_binding_session"],
            await database.UniqueIndexNamesAsync()
        );
        Assert.True(await database.IndexIsPartialAsync("ux_project_conversation_binding_active"));
        Assert.False(await database.IndexIsPartialAsync("ux_project_conversation_binding_session"));
    }

    private static async Task Downgrade_SingleRecordPerGroup_RestoresOriginalSchema(
        MigrationDatabase database,
        bool useScript
    )
    {
        var token = TestContext.Current.CancellationToken;
        var (previous, latest) = await MigrateToLatestAsync(database);
        await using (var context = database.CreateContext())
        {
            var conversationId = Guid.CreateVersion7();
            await InsertBindingAsync(context, conversationId, "codex", "session-a", isActive: false);
            await InsertBindingAsync(context, conversationId, "pi", "session-b", isActive: true);
        }

        await DowngradeAsync(database, previous, latest, useScript);

        await using var verify = database.CreateContext();
        Assert.Equal([previous], await verify.Database.GetAppliedMigrationsAsync(token));
        Assert.False(await database.ColumnExistsAsync("is_active"));
        Assert.Equal([database.OriginalIndexName], await database.UniqueIndexNamesAsync());
        Assert.Equal(["session-a", "session-b"], await ReadSessionIdsAsync(verify));
    }

    private static async Task Downgrade_MultipleRecordsInGroup_FailsAndKeepsCurrentSchema(
        MigrationDatabase database,
        bool useScript
    )
    {
        var token = TestContext.Current.CancellationToken;
        var (previous, latest) = await MigrateToLatestAsync(database);
        await using (var context = database.CreateContext())
        {
            var conversationId = Guid.CreateVersion7();
            await InsertBindingAsync(context, conversationId, "codex", "session-a", isActive: false);
            await InsertBindingAsync(context, conversationId, "codex", "session-b", isActive: true);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => DowngradeAsync(database, previous, latest, useScript));

        await using var verify = database.CreateContext();
        Assert.Equal([previous, latest], await verify.Database.GetAppliedMigrationsAsync(token));
        Assert.True(await database.ColumnExistsAsync("is_active"));
        Assert.Equal(
            ["ux_project_conversation_binding_active", "ux_project_conversation_binding_session"],
            await database.UniqueIndexNamesAsync()
        );
        Assert.Equal(["session-a", "session-b"], await ReadSessionIdsAsync(verify));
        Assert.Equal([false, true], await ReadActiveFlagsAsync(verify));
    }

    private static async Task<(string Previous, string Latest)> MigrateToLatestAsync(MigrationDatabase database)
    {
        await using var context = database.CreateContext();
        var migrations = GetMigrations(context);
        await context.GetService<IMigrator>().MigrateAsync(migrations.Latest, TestContext.Current.CancellationToken);
        return migrations;
    }

    /// <summary>
    /// 通过 EF 迁移执行器或执行生成的降级 SQL 回退到上一个迁移；SQL 在第一条失败语句处停止，未提交的事务随连接关闭撤销。
    /// Rolls back to the previous migration through the EF migrator or by running the generated downgrade SQL; the SQL stops at the first failing statement, and the uncommitted transaction is undone when the connection closes.
    /// </summary>
    private static async Task DowngradeAsync(MigrationDatabase database, string previous, string latest, bool useScript)
    {
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        if (useScript)
        {
            await database.ExecuteScriptAsync(migrator.GenerateScript(latest, previous));
            return;
        }

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
    }

    private static (string Previous, string Latest) GetMigrations(AgwDbContext context)
    {
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.EndsWith(MigrationSuffix, migrations[^1], StringComparison.Ordinal);
        return (migrations[^2], migrations[^1]);
    }

    private static string OriginalIndexName(DatabaseProvider provider) =>
        provider == DatabaseProvider.Postgres
            ? "ix_project_conversation_binding_project_conversation_id_agent_"
            : "ix_project_conversation_binding_project_conversation_id_agent_id_external_agent_name";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (
            var index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    private static Task InsertLegacyBindingAsync(
        AgwDbContext context,
        Guid conversationId,
        string externalAgentName,
        string providerSessionId
    ) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project_conversation_binding (id, project_conversation_id, agent_id, external_agent_name, provider_session_id, create_time, create_by) VALUES ({Guid.CreateVersion7()}, {conversationId}, {Guid.Empty}, {externalAgentName}, {providerSessionId}, {DateTimeOffset.UtcNow}, {"tester"})",
            TestContext.Current.CancellationToken
        );

    private static Task InsertBindingAsync(
        AgwDbContext context,
        Guid conversationId,
        string externalAgentName,
        string providerSessionId,
        bool isActive
    ) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project_conversation_binding (id, project_conversation_id, agent_id, external_agent_name, provider_session_id, is_active, create_time, create_by) VALUES ({Guid.CreateVersion7()}, {conversationId}, {Guid.Empty}, {externalAgentName}, {providerSessionId}, {isActive}, {DateTimeOffset.UtcNow}, {"tester"})",
            TestContext.Current.CancellationToken
        );

    private static Task<List<string>> ReadSessionIdsAsync(AgwDbContext context) =>
        context
            .Database.SqlQueryRaw<string>(
                "SELECT provider_session_id AS \"Value\" FROM project_conversation_binding ORDER BY provider_session_id"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

    private static Task<List<bool>> ReadActiveFlagsAsync(AgwDbContext context) =>
        context
            .Database.SqlQueryRaw<bool>(
                "SELECT is_active AS \"Value\" FROM project_conversation_binding ORDER BY provider_session_id"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

    private abstract class MigrationDatabase : IAsyncDisposable
    {
        public abstract string OriginalIndexName { get; }

        public abstract AgwDbContext CreateContext();

        public abstract Task ExecuteScriptAsync(string script);

        public abstract Task<bool> ColumnExistsAsync(string column);

        /// <summary>
        /// 绑定表上全部唯一索引的名称，按名称排序。
        /// The names of every unique index on the binding table, ordered by name.
        /// </summary>
        public abstract Task<List<string>> UniqueIndexNamesAsync();

        public abstract Task<bool> IndexIsPartialAsync(string index);

        public abstract ValueTask DisposeAsync();
    }

    private sealed class SqliteMigrationDatabase : MigrationDatabase
    {
        private readonly string _path;
        private readonly string _connectionString;

        private SqliteMigrationDatabase(string path)
        {
            _path = path;
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                ForeignKeys = false,
            }.ToString();
        }

        public override string OriginalIndexName =>
            ProjectConversationBindingMigrationTests.OriginalIndexName(DatabaseProvider.Sqlite);

        public static Task<SqliteMigrationDatabase> CreateAsync()
        {
            var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data"));
            return Task.FromResult(
                new SqliteMigrationDatabase(
                    Path.Combine(directory.FullName, $"binding-migration-{Guid.NewGuid():N}.db")
                )
            );
        }

        public override AgwDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Sqlite, _connectionString);
            return new AgwDbContext(options.Options);
        }

        public override async Task ExecuteScriptAsync(string script)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public override async Task<bool> ColumnExistsAsync(string column) =>
            await ScalarAsync(
                "SELECT COUNT(*) FROM pragma_table_info('project_conversation_binding') WHERE name = $value",
                column
            ) == 1;

        public override async Task<List<string>> UniqueIndexNamesAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM pragma_index_list('project_conversation_binding') WHERE [unique] = 1 AND origin = 'c' ORDER BY name";
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        public override async Task<bool> IndexIsPartialAsync(string index) =>
            await ScalarAsync(
                "SELECT partial FROM pragma_index_list('project_conversation_binding') WHERE name = $value",
                index
            ) == 1;

        private async Task<long> ScalarAsync(string sql, string value)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        public override ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PostgresMigrationDatabase : MigrationDatabase
    {
        private readonly NpgsqlConnectionStringBuilder _admin;
        private readonly string _database;
        private readonly string _connectionString;

        private PostgresMigrationDatabase(NpgsqlConnectionStringBuilder admin, string database)
        {
            _admin = admin;
            _database = database;
            _connectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = database,
                Pooling = false,
            }.ConnectionString;
        }

        public override string OriginalIndexName =>
            ProjectConversationBindingMigrationTests.OriginalIndexName(DatabaseProvider.Postgres);

        public static async Task<PostgresMigrationDatabase> CreateAsync()
        {
            var admin = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
            )
            {
                Pooling = false,
            };
            var database = $"agw_binding_migration_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return new PostgresMigrationDatabase(admin, database);
        }

        public override AgwDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Postgres, _connectionString);
            return new AgwDbContext(options.Options);
        }

        public override async Task ExecuteScriptAsync(string script)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public override async Task<bool> ColumnExistsAsync(string column) =>
            await ScalarAsync(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'project_conversation_binding' AND column_name = @value",
                column
            ) == 1;

        public override async Task<List<string>> UniqueIndexNamesAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes WHERE tablename = 'project_conversation_binding' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexname <> 'pk_project_conversation_binding' ORDER BY indexname",
                connection
            );
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        public override async Task<bool> IndexIsPartialAsync(string index) =>
            await ScalarAsync(
                "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @value AND indexdef LIKE '% WHERE %'",
                index
            ) == 1;

        private async Task<long> ScalarAsync(string sql, string value)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("value", value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        public override async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)",
                connection
            );
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
