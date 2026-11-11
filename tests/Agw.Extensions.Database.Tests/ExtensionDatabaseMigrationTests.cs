using System.Data;
using System.Data.Common;
using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Database;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed partial class ExtensionDatabaseMigrationTests
{
    private static readonly Guid ExtensionId = ExtensionDatabaseTestHost.ExtensionId;
    private static readonly string TablePrefix = $"zext_{ExtensionId:N}_";
    private static readonly string TableName = $"{TablePrefix}business_record";
    private static readonly string IndexName = $"{TablePrefix}ix_business_record_title";
    private static readonly string InboxTableName = $"{TablePrefix}__inbox";
    private static readonly string OutboxTableName = $"{TablePrefix}__outbox";
    private static readonly string OutboxIdempotencyIndexName = $"{TablePrefix}uq_outbox_key";

    [Fact]
    public async Task PrepareAsync_MigratesSqliteAndRetainsCompatibleSchema()
    {
        await RunMigrationLifecycleAsync(ExtensionDatabaseProvider.Sqlite, null);
    }

    [Fact]
    public async Task PrepareAsync_RecoversAbandonedSqliteMigrationLockUnderPlatformExclusiveLock()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadAndActivateAsync(
            TestContext.Current.CancellationToken
        );
        ExtensionDatabaseLockCoordinator coordinator =
            host.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>();
        await using var generation = CreateGeneration(host, database, runtimeGeneration, runtimeGeneration.ReleaseHash);
        await using var scope = generation.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestMigrationContext>();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        IMigrationsDatabaseLock abandonedLock;
        using (scope.ServiceProvider.GetRequiredService<ExtensionDatabaseTransactionInterceptor>().EnterMigration())
        {
            abandonedLock = await context
                .GetService<IHistoryRepository>()
                .AcquireDatabaseLockAsync(TestContext.Current.CancellationToken);
        }

        await context.Database.CloseConnectionAsync();
        await using DbConnection connection = await generation.Generation.CreateConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using IAsyncDisposable migrationLock = await coordinator.AcquireMigrationAccessAsync(
            ExtensionId,
            ExtensionDatabaseProvider.Sqlite,
            connection,
            TestContext.Current.CancellationToken
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await generation
            .Generation.PrepareAsync(connection, timeout.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

        GC.KeepAlive(abandonedLock);
        await AssertMigratedDataAsync(database, connection);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task PrepareAsync_MigratesPostgreSqlAndRetainsCompatibleSchema()
    {
        string connectionString = RequirePostgresConnectionString();
        await RunMigrationLifecycleAsync(ExtensionDatabaseProvider.PostgreSql, connectionString);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ValidateOperations_RejectsUnsafeNpgsqlIndexMethod()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(
            ExtensionDatabaseProvider.PostgreSql,
            RequirePostgresConnectionString()
        );
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateGeneration(host, database, runtimeGeneration, runtimeGeneration.ReleaseHash);
        await using var scope = generation.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestMigrationContext>();
        var operation = new CreateIndexOperation
        {
            Name = IndexName,
            Table = TableName,
            Columns = ["title"],
        };
        operation["Npgsql:IndexMethod"] = "btree; DROP TABLE agw_user; --";

        AgwException exception = Assert.Throws<AgwException>(() =>
            ExtensionMigrationValidator.ValidateOperations(
                [operation],
                new ExtensionDatabaseSchemaMetadata(ExtensionDatabaseProvider.PostgreSql, TablePrefix),
                context.Model.GetMaxIdentifierLength(),
                context.GetService<IRelationalTypeMappingSource>()
            )
        );

        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    private static async Task RunMigrationLifecycleAsync(
        ExtensionDatabaseProvider provider,
        string? administrativeConnectionString
    )
    {
        await using var database = await MigrationTestDatabase.CreateAsync(provider, administrativeConnectionString);
        await using var migrationDatabase = await MigrationTestDatabase.CreateAsync(
            provider,
            administrativeConnectionString
        );
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database, packageVersion: "1.0.0");
        ExtensionDatabaseLockCoordinator coordinator =
            host.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>();
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadAndActivateAsync(
            TestContext.Current.CancellationToken
        );

        await using (
            var firstGeneration = CreateGeneration(
                host,
                migrationDatabase,
                runtimeGeneration,
                runtimeGeneration.ReleaseHash
            )
        )
        {
            await firstGeneration.Generation.ValidateModelAsync(TestContext.Current.CancellationToken);
            await using DbConnection connection = await firstGeneration.Generation.CreateConnectionAsync(
                TestContext.Current.CancellationToken
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using IAsyncDisposable migrationLock = await coordinator.AcquireMigrationAccessAsync(
                ExtensionId,
                provider,
                connection,
                TestContext.Current.CancellationToken
            );
            await firstGeneration.Generation.PrepareAsync(connection, TestContext.Current.CancellationToken);
            await AssertMigratedDataAsync(migrationDatabase, connection);
        }

        Assert.True(
            await host.Runtime.DrainAsync(
                runtimeGeneration.GenerationId,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            )
        );
        await host.Runtime.UnloadAsync(
            runtimeGeneration.GenerationId,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
        await using var reinstallHost = await ExtensionDatabaseTestHost.CreateAsync(database, packageVersion: "2.0.0");
        ExtensionDatabaseLockCoordinator reinstallCoordinator =
            reinstallHost.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>();
        runtimeGeneration = await reinstallHost.LoadAndActivateAsync(TestContext.Current.CancellationToken);
        await using (
            var reinstallGeneration = CreateGeneration(
                reinstallHost,
                migrationDatabase,
                runtimeGeneration,
                runtimeGeneration.ReleaseHash
            )
        )
        {
            await reinstallGeneration.Generation.ValidateModelAsync(TestContext.Current.CancellationToken);
            await using DbConnection connection = await reinstallGeneration.Generation.CreateConnectionAsync(
                TestContext.Current.CancellationToken
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using IAsyncDisposable migrationLock = await reinstallCoordinator.AcquireMigrationAccessAsync(
                ExtensionId,
                provider,
                connection,
                TestContext.Current.CancellationToken
            );
            await reinstallGeneration.Generation.PrepareAsync(connection, TestContext.Current.CancellationToken);
            await AssertMigratedDataAsync(migrationDatabase, connection);
        }

        await InsertUnknownMigrationAsync(migrationDatabase);
        await using var incompatibleGeneration = CreateGeneration(
            reinstallHost,
            migrationDatabase,
            runtimeGeneration,
            runtimeGeneration.ReleaseHash
        );
        await using DbConnection incompatibleConnection = await incompatibleGeneration.Generation.CreateConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await incompatibleConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using IAsyncDisposable incompatibleLock = await reinstallCoordinator.AcquireMigrationAccessAsync(
            ExtensionId,
            provider,
            incompatibleConnection,
            TestContext.Current.CancellationToken
        );
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            incompatibleGeneration
                .Generation.PrepareAsync(incompatibleConnection, TestContext.Current.CancellationToken)
                .AsTask()
        );
        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    private static ExtensionDatabaseFactoryFixture CreateGeneration(
        ExtensionDatabaseTestHost host,
        MigrationTestDatabase database,
        ExtensionGenerationSnapshot runtimeGeneration,
        string releaseHash
    )
    {
        var options = new ExtensionDatabaseOptions
        {
            ExtensionId = ExtensionId,
            GenerationId = runtimeGeneration.GenerationId.GenerationId,
            Name = "migration-test",
            Version = "1.0.0",
            ReleaseHash = releaseHash,
            Epoch = runtimeGeneration.Epoch,
            TablePrefix = TablePrefix,
            Provider = database.Provider,
            ConnectionString = database.ConnectionString,
        };
        return host.CreateSdkGeneration<TestMigrationContext, MigrationAssemblyMarker>(options);
    }

    private static async Task AssertMigratedDataAsync(MigrationTestDatabase database, DbConnection connection)
    {
        string table = Quote(database.Provider, database.ConnectionString, TableName);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT title FROM {table} WHERE id = 1";
            Assert.Equal("updated", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = 2";
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            if (database.Provider == ExtensionDatabaseProvider.Sqlite)
            {
                command.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name)";
            }
            else
            {
                command.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_indexes WHERE schemaname = current_schema() AND indexname = @name)";
            }
            AddParameter(command, "@name", IndexName, DbType.String);
            Assert.True(Convert.ToBoolean(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }

        await using var history = connection.CreateCommand();
        history.CommandText =
            $"SELECT COUNT(*) FROM {Quote(database.Provider, database.ConnectionString, $"{TablePrefix}__ef_migrations_history")}";
        Assert.Equal(3L, await history.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        if (database.Provider == ExtensionDatabaseProvider.Sqlite)
        {
            await using var lockTable = connection.CreateCommand();
            lockTable.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%EFMigrationsLock'";
            Assert.Equal(
                $"{TablePrefix}__EFMigrationsLock",
                await lockTable.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            );
        }
    }

    private static async Task InsertUnknownMigrationAsync(MigrationTestDatabase database)
    {
        await using DbConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        string historyTable = Quote(
            database.Provider,
            database.ConnectionString,
            $"{TablePrefix}__ef_migrations_history"
        );
        IReadOnlyList<string> columns = await GetColumnsAsync(database.Provider, connection, historyTable);
        string migrationIdColumn = Quote(
            database.Provider,
            database.ConnectionString,
            columns.Single(column => column.Contains("migration", StringComparison.OrdinalIgnoreCase))
        );
        string productVersionColumn = Quote(
            database.Provider,
            database.ConnectionString,
            columns.Single(column => column.Contains("product", StringComparison.OrdinalIgnoreCase))
        );
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {historyTable} ({migrationIdColumn}, {productVersionColumn}) VALUES (@id, @product)";
        AddParameter(command, "@id", "20990101000000_NotPackaged", DbType.String);
        AddParameter(command, "@product", "10.0.12", DbType.String);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(
        ExtensionDatabaseProvider provider,
        DbConnection connection,
        string historyTable
    )
    {
        await using var command = connection.CreateCommand();
        if (provider == ExtensionDatabaseProvider.Sqlite)
        {
            command.CommandText = $"PRAGMA table_info({historyTable})";
        }
        else
        {
            command.CommandText =
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table";
            AddParameter(command, "@table", historyTable.Trim('"'), DbType.String);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(provider == ExtensionDatabaseProvider.Sqlite ? 1 : 0));
        }

        return columns;
    }

    private static string Quote(ExtensionDatabaseProvider provider, string connectionString, string identifier)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();
        if (provider == ExtensionDatabaseProvider.Sqlite)
        {
            optionsBuilder.UseSqlite(connectionString);
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionString);
        }

        using var context = new DbContext(optionsBuilder.Options);
        return context.Database.GetService<ISqlGenerationHelper>().DelimitIdentifier(identifier);
    }

    private static void AddParameter(DbCommand command, string name, object value, DbType type)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static string RequirePostgresConnectionString()
    {
        string repositoryRoot = FindRepositoryRoot();
        string path = Path.Combine(repositoryRoot, ".scratch", "postgres-tests.json");
        if (!File.Exists(path))
        {
            Assert.Skip("PostgreSQL test configuration is not present.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        string? connectionString = document.RootElement.GetProperty("ConnectionString").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        return connectionString;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Agw solution root.");
    }

    private static string GetTextType(string provider) =>
        provider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "text";

    private static string GetDateTimeOffsetType(string provider) =>
        provider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "timestamp with time zone";

    internal sealed class MigrationTestDatabase : IAsyncDisposable
    {
        private readonly string? _administrativeConnectionString;
        private readonly string? _databaseName;
        private readonly SqliteConnection? _sqliteKeepAlive;
        private string? _sqliteDirectory;

        private MigrationTestDatabase(
            ExtensionDatabaseProvider provider,
            string connectionString,
            SqliteConnection? sqliteKeepAlive,
            string? administrativeConnectionString,
            string? databaseName
        )
        {
            Provider = provider;
            ConnectionString = connectionString;
            _sqliteKeepAlive = sqliteKeepAlive;
            _administrativeConnectionString = administrativeConnectionString;
            _databaseName = databaseName;
        }

        public ExtensionDatabaseProvider Provider { get; }

        public string ConnectionString { get; }

        public static async Task<MigrationTestDatabase> CreateAsync(
            ExtensionDatabaseProvider provider,
            string? administrativeConnectionString,
            bool sqliteFile = false
        )
        {
            if (provider == ExtensionDatabaseProvider.Sqlite)
            {
                if (sqliteFile)
                {
                    string directory = Path.Combine(
                        AppContext.BaseDirectory,
                        $"extension-wal-{Guid.CreateVersion7():N}"
                    );
                    Directory.CreateDirectory(directory);
                    string fileConnectionString = new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(directory, "extension.db"),
                        DefaultTimeout = 1,
                        Pooling = false,
                    }.ConnectionString;
                    var fileKeeper = new SqliteConnection(fileConnectionString);
                    await fileKeeper.OpenAsync(TestContext.Current.CancellationToken);
                    await using var command = fileKeeper.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=WAL";
                    Assert.Equal("wal", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                    return new MigrationTestDatabase(provider, fileConnectionString, fileKeeper, null, null)
                    {
                        _sqliteDirectory = directory,
                    };
                }
                string connectionString =
                    $"Data Source=extension-migration-{Guid.CreateVersion7():N};Mode=Memory;Cache=Shared";
                var keeper = new SqliteConnection(connectionString);
                await keeper.OpenAsync(TestContext.Current.CancellationToken);
                return new MigrationTestDatabase(provider, connectionString, keeper, null, null);
            }

            string adminConnectionString = administrativeConnectionString ?? RequirePostgresConnectionString();
            var adminSettings = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };
            string databaseName = $"agw_extension_migration_{Guid.CreateVersion7():N}";
            await using (var admin = new NpgsqlConnection(adminSettings.ConnectionString))
            {
                await admin.OpenAsync(TestContext.Current.CancellationToken);
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)}";
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var databaseSettings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
            {
                Database = databaseName,
            };
            return new MigrationTestDatabase(
                provider,
                databaseSettings.ConnectionString,
                null,
                adminSettings.ConnectionString,
                databaseName
            );
        }

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            DbConnection connection =
                Provider == ExtensionDatabaseProvider.Sqlite
                    ? new SqliteConnection(ConnectionString)
                    : new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqliteKeepAlive is not null)
            {
                await _sqliteKeepAlive.DisposeAsync();
                if (_sqliteDirectory is not null)
                    Directory.Delete(_sqliteDirectory, recursive: true);
            }

            if (_administrativeConnectionString is not null && _databaseName is not null)
            {
                await using var admin = new NpgsqlConnection(_administrativeConnectionString);
                await admin.OpenAsync(CancellationToken.None);
                await using var drop = admin.CreateCommand();
                drop.CommandText =
                    $"DROP DATABASE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(_databaseName)}";
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private sealed class MigrationAssemblyMarker { }

    public sealed class TestMigrationContext : ExtensionDbContext
    {
        public TestMigrationContext(
            DbContextOptions<TestMigrationContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        public DbSet<MigrationRecord> Records => Set<MigrationRecord>();

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
        {
            ConfigureTargetModel(modelBuilder, true, true, Database.ProviderName!);
        }
    }

    public sealed class MigrationRecord : IExtensionOwnedEntity
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }

    [DbContext(typeof(TestMigrationContext))]
    [Migration("20261106000100_Initial")]
    public sealed class InitialMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string provider = ActiveProvider!;
            string textType = GetTextType(provider);
            string timeType = GetDateTimeOffsetType(provider);
            migrationBuilder.CreateTable(
                name: TableName,
                columns: table => new
                {
                    id = table.Column<int>(
                        type: provider == "Microsoft.EntityFrameworkCore.Sqlite" ? "INTEGER" : "integer",
                        nullable: false
                    ),
                    title = table.Column<string>(type: textType, nullable: false),
                    create_by = table.Column<string>(type: textType, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: timeType, nullable: false),
                    update_by = table.Column<string>(type: textType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: timeType, nullable: true),
                },
                constraints: table => table.PrimaryKey($"pk_{TableName}", row => row.id)
            );
            CreateExtensionMessagingTables(migrationBuilder, provider);
            migrationBuilder.InsertData(
                TableName,
                ["id", "title", "create_by", "create_time", "update_by", "update_time"],
                new object?[,]
                {
                    { 1, "initial", "alice", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null },
                }
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropExtensionMessagingTables(migrationBuilder);
            migrationBuilder.DropTable(TableName);
        }

        protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
            ConfigureTargetModel(modelBuilder, false, false, ActiveProvider!);
    }

    [DbContext(typeof(TestMigrationContext))]
    [Migration("20261106000200_AlterTitle")]
    public sealed class AlterTitleMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string textType = GetTextType(ActiveProvider!);
            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: TableName,
                type: textType,
                nullable: true,
                oldClrType: typeof(string),
                oldType: textType,
                oldNullable: false
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            string textType = GetTextType(ActiveProvider!);
            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: TableName,
                type: textType,
                nullable: false,
                oldClrType: typeof(string),
                oldType: textType,
                oldNullable: true
            );
        }

        protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
            ConfigureTargetModel(modelBuilder, true, false, ActiveProvider!);
    }

    [DbContext(typeof(TestMigrationContext))]
    [Migration("20261106000300_IndexAndData")]
    public sealed class IndexAndDataMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(IndexName, TableName, "title");
            migrationBuilder.InsertData(
                TableName,
                ["id", "title", "create_by", "create_time", "update_by", "update_time"],
                new object?[,]
                {
                    { 2, "delete me", "alice", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null },
                }
            );
            migrationBuilder.UpdateData(TableName, ["id"], [1], ["title"], ["updated"]);
            migrationBuilder.DeleteData(TableName, ["id"], [2]);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropIndex(IndexName, TableName);

        protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
            ConfigureTargetModel(modelBuilder, true, true, ActiveProvider!);
    }

    private static void ConfigureTargetModel(
        ModelBuilder modelBuilder,
        bool titleNullable,
        bool withIndex,
        string provider
    )
    {
        string textType = GetTextType(provider);
        string timeType = GetDateTimeOffsetType(provider);
        modelBuilder.Entity<MigrationRecord>(entity =>
        {
            entity.ToTable(TableName);
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasColumnName("id");
            entity
                .Property(record => record.Title)
                .HasColumnName("title")
                .HasColumnType(textType)
                .IsRequired(!titleNullable);
            entity.Property(record => record.CreateBy).HasColumnName("create_by").HasColumnType(textType);
            entity.Property(record => record.CreateTime).HasColumnName("create_time").HasColumnType(timeType);
            entity.Property(record => record.UpdateBy).HasColumnName("update_by").HasColumnType(textType);
            entity.Property(record => record.UpdateTime).HasColumnName("update_time").HasColumnType(timeType);
            if (withIndex)
            {
                entity.HasIndex(record => record.Title).HasDatabaseName(IndexName);
            }
        });
        modelBuilder.UseExtensionMessaging(TablePrefix, timeType);
    }

    private static void CreateExtensionMessagingTables(MigrationBuilder migrationBuilder, string provider)
    {
        string textType = GetTextType(provider);
        OperationBuilder<AddColumnOperation> Timestamp(ColumnsBuilder table, bool nullable) =>
            provider == "Microsoft.EntityFrameworkCore.Sqlite"
                ? table.Column<long>(type: "INTEGER", nullable: nullable)
                : table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: nullable);
        string guidType = provider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "uuid";
        migrationBuilder.CreateTable(
            name: InboxTableName,
            columns: table => new
            {
                delivery_id = table.Column<Guid>(type: guidType, nullable: false),
                create_by = table.Column<string>(type: textType, nullable: false),
                create_time = Timestamp(table, false),
                update_by = table.Column<string>(type: textType, nullable: true),
                update_time = Timestamp(table, true),
                processed_at = Timestamp(table, false),
                result_json = table.Column<string>(type: textType, nullable: false),
            },
            constraints: table => table.PrimaryKey($"pk_{InboxTableName}", row => row.delivery_id)
        );
        migrationBuilder.CreateTable(
            name: OutboxTableName,
            columns: table => new
            {
                id = table.Column<Guid>(type: guidType, nullable: false),
                create_by = table.Column<string>(type: textType, nullable: false),
                create_time = Timestamp(table, false),
                update_by = table.Column<string>(type: textType, nullable: true),
                update_time = Timestamp(table, true),
                kind = table.Column<string>(type: textType, nullable: false),
                idempotency_key = table.Column<string>(type: textType, nullable: false),
                payload_json = table.Column<string>(type: textType, nullable: false),
                state = table.Column<string>(type: textType, nullable: false),
                result_json = table.Column<string>(type: textType, nullable: true),
                error_code = table.Column<int>(type: "integer", nullable: true),
                attempt = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = Timestamp(table, true),
                completed_at = Timestamp(table, true),
                last_error = table.Column<string>(type: textType, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{OutboxTableName}", row => row.id)
        );
        migrationBuilder.CreateIndex(
            name: OutboxIdempotencyIndexName,
            table: OutboxTableName,
            column: "idempotency_key",
            unique: true
        );
    }

    private static void DropExtensionMessagingTables(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(OutboxTableName);
        migrationBuilder.DropTable(InboxTableName);
    }
}
