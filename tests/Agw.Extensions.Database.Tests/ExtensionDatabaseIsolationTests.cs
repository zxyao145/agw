using System.Data;
using System.Data.Common;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed partial class ExtensionDatabaseMigrationTests
{
    [Fact]
    public Task ValidateModelAsync_IndexOperationsCannotTargetAnotherExtension_Sqlite() =>
        AssertIndexOperationsCannotTargetAnotherExtensionAsync(ExtensionDatabaseProvider.Sqlite, null);

    [Fact]
    [Trait("Category", "Postgres")]
    public Task ValidateModelAsync_IndexOperationsCannotTargetAnotherExtension_PostgreSql() =>
        AssertIndexOperationsCannotTargetAnotherExtensionAsync(
            ExtensionDatabaseProvider.PostgreSql,
            RequirePostgresConnectionString()
        );

    private static async Task AssertIndexOperationsCannotTargetAnotherExtensionAsync(
        ExtensionDatabaseProvider provider,
        string? administrativeConnectionString
    )
    {
        await using var database = await MigrationTestDatabase.CreateAsync(provider, administrativeConnectionString);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await CreateCoreIndexAsync(database);
        await AssertIndexMigrationRejectedAsync<DropIndexContext>(host, runtimeGeneration);
        await AssertCoreIndexExistsAsync(database);
        await AssertIndexMigrationRejectedAsync<RenameIndexContext>(host, runtimeGeneration);
        await AssertCoreIndexExistsAsync(database);
        await AssertIndexMigrationRejectedAsync<CreateIndexContext>(host, runtimeGeneration);
        await AssertCoreIndexExistsAsync(database);
    }

    [Fact]
    public async Task ValidateModelAsync_ViewMappingToOtherNamespace_IsRejected()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<ViewMappedContext>(host, runtimeGeneration);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            generation.Generation.ValidateModelAsync(TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    [Fact]
    public async Task ValidateModelAsync_SqlQueryMappingToCoreTable_IsRejected()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<SqlQueryMappedContext>(host, runtimeGeneration);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            generation.Generation.ValidateModelAsync(TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    [Fact]
    public async Task ValidateModelAsync_NavigationRelationshipsRetainMetadataWithoutPhysicalForeignKeys()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<NavigationContext>(host, runtimeGeneration);
        await generation.Generation.ValidateModelAsync(TestContext.Current.CancellationToken);

        await using var scope = generation.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NavigationContext>();
        var relationalModel = context.Model.GetRelationalModel();
        var noteTable = Assert.Single(
            relationalModel.Tables,
            table => table.Name.EndsWith("navigation_note", StringComparison.Ordinal)
        );
        Assert.Single(noteTable.ForeignKeyConstraints);

        var designTimeModel = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var migrationOperations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, designTimeModel);
        Assert.DoesNotContain(
            migrationOperations,
            operation => operation is AddForeignKeyOperation or DropForeignKeyOperation
        );
        Assert.All(
            migrationOperations.OfType<CreateTableOperation>(),
            operation => Assert.Empty(operation.ForeignKeys)
        );
    }

    [Fact]
    public async Task SqlGenerator_SqliteRebuildWithNavigationModelDoesNotCreatePhysicalForeignKey()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<NavigationContext>(host, runtimeGeneration);
        await using var scope = generation.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NavigationContext>();
        var targetModel = context.GetService<IDesignTimeModel>().Model;
        IMigrationsSqlGenerator generator = context.Database.GetService<IMigrationsSqlGenerator>();
        string tableName = $"{TablePrefix}navigation_note";
        IReadOnlyList<MigrationCommand> commands = generator.Generate(
            [
                new AlterColumnOperation
                {
                    Name = "value",
                    Table = tableName,
                    ClrType = typeof(string),
                    ColumnType = "TEXT",
                    IsNullable = true,
                    OldColumn = new AddColumnOperation
                    {
                        Name = "value",
                        Table = tableName,
                        ClrType = typeof(string),
                        ColumnType = "TEXT",
                        IsNullable = false,
                    },
                },
            ],
            targetModel,
            MigrationsSqlGenerationOptions.Default
        );

        Assert.Contains(commands, command => command.CommandText.Contains("ef_temp_", StringComparison.Ordinal));
        Assert.DoesNotContain(
            commands,
            command => command.CommandText.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task SqlGenerator_RejectsUnsupportedOperationsAndUnsafeColumnSql()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<NavigationContext>(host, runtimeGeneration);
        await using var scope = generation.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NavigationContext>();
        var targetModel = context.GetService<IDesignTimeModel>().Model;
        IMigrationsSqlGenerator generator = context.Database.GetService<IMigrationsSqlGenerator>();
        string tableName = $"{TablePrefix}navigation_note";
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "UPDATE agw_user SET name = 'changed'" },
            new AddForeignKeyOperation
            {
                Name = "fk_zext_test",
                Table = tableName,
                Columns = ["root_id"],
                PrincipalTable = $"{TablePrefix}navigation_root",
                PrincipalColumns = ["id"],
            },
            new AddCheckConstraintOperation
            {
                Name = "ck_zext_test",
                Table = tableName,
                Sql = "1 = 1",
            },
            new AddColumnOperation
            {
                Name = "unsafe_type",
                Table = tableName,
                ClrType = typeof(string),
                ColumnType = "TEXT); DROP TABLE agw_user; --",
                IsNullable = true,
            },
            new AddColumnOperation
            {
                Name = "unsafe_collation",
                Table = tableName,
                ClrType = typeof(string),
                ColumnType = "TEXT",
                Collation = "BINARY); DROP TABLE agw_user; --",
                IsNullable = true,
            },
            new AddColumnOperation
            {
                Name = "unsafe_default",
                Table = tableName,
                ClrType = typeof(string),
                ColumnType = "TEXT",
                DefaultValueSql = "1); DROP TABLE agw_user; --",
                IsNullable = true,
            },
            new AddColumnOperation
            {
                Name = "unsafe_computed",
                Table = tableName,
                ClrType = typeof(string),
                ColumnType = "TEXT",
                ComputedColumnSql = "(SELECT 1); DROP TABLE agw_user; --",
                IsNullable = true,
            },
            new AddPrimaryKeyOperation
            {
                Name = "shared_primary",
                Table = tableName,
                Columns = ["id"],
            },
            new AddUniqueConstraintOperation
            {
                Name = "shared_unique",
                Table = tableName,
                Columns = ["value"],
            },
        ];

        foreach (MigrationOperation operation in operations)
        {
            var exception = Assert.Throws<AgwException>(() =>
                generator.Generate([operation], targetModel, MigrationsSqlGenerationOptions.Default)
            );
            Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
        }
    }

    [Fact]
    public async Task ValidateModelAsync_UnprefixedPrimaryAndUniqueConstraintNamesAreRejected()
    {
        await using var database = await MigrationTestDatabase.CreateAsync(ExtensionDatabaseProvider.Sqlite, null);
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database);
        ExtensionGenerationSnapshot runtimeGeneration = await host.LoadGenerationAsync(
            TestContext.Current.CancellationToken
        );
        await using var generation = CreateValidationGeneration<UnprefixedConstraintContext>(host, runtimeGeneration);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            generation.Generation.ValidateModelAsync(TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    private static async Task AssertIndexMigrationRejectedAsync<TContext>(
        ExtensionDatabaseTestHost host,
        ExtensionGenerationSnapshot runtimeGeneration
    )
        where TContext : ExtensionDbContext
    {
        await using var generation = CreateValidationGeneration<TContext>(host, runtimeGeneration);
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            generation.Generation.ValidateModelAsync(TestContext.Current.CancellationToken).AsTask()
        );
        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    private static ExtensionDatabaseFactoryFixture CreateValidationGeneration<TContext>(
        ExtensionDatabaseTestHost host,
        ExtensionGenerationSnapshot runtimeGeneration
    )
        where TContext : ExtensionDbContext
    {
        var options = new ExtensionDatabaseOptions
        {
            ExtensionId = ExtensionId,
            GenerationId = runtimeGeneration.GenerationId.GenerationId,
            Name = "migration-isolation-test",
            Version = "1.0.0",
            ReleaseHash = runtimeGeneration.ReleaseHash,
            Epoch = runtimeGeneration.Epoch,
            TablePrefix = TablePrefix,
            Provider = host.Provider,
            ConnectionString = host.ConnectionString,
        };
        return host.CreateSdkGeneration<TContext, IsolationMigrationAssemblyMarker>(options);
    }

    private static async Task CreateCoreIndexAsync(MigrationTestDatabase database)
    {
        await using DbConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE core_record (id integer PRIMARY KEY, email text NOT NULL)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "CREATE INDEX core_shared_index ON core_record (email)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertCoreIndexExistsAsync(MigrationTestDatabase database)
    {
        await using DbConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            database.Provider == ExtensionDatabaseProvider.Sqlite
                ? "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name)"
                : "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_indexes WHERE schemaname = current_schema() AND indexname = @name)";
        AddParameter(command, "@name", "core_shared_index", DbType.String);
        Assert.True(Convert.ToBoolean(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    private sealed class IsolationMigrationAssemblyMarker { }

    public sealed class DropIndexContext : IndexIsolationContextBase
    {
        public DropIndexContext(
            DbContextOptions<DropIndexContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }
    }

    public sealed class RenameIndexContext : IndexIsolationContextBase
    {
        public RenameIndexContext(
            DbContextOptions<RenameIndexContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }
    }

    public sealed class CreateIndexContext : IndexIsolationContextBase
    {
        public CreateIndexContext(
            DbContextOptions<CreateIndexContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }
    }

    public abstract class IndexIsolationContextBase : ExtensionDbContext
    {
        protected IndexIsolationContextBase(
            DbContextOptions options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<IndexIsolationRecord>(entity =>
            {
                entity.ToTable("business_record");
                entity.HasKey(record => record.Id);
                entity.Property(record => record.Id).HasColumnName("id");
                entity.Property(record => record.Email).HasColumnName("email");
            });
        }
    }

    public sealed class IndexIsolationRecord : IExtensionOwnedEntity
    {
        public int Id { get; set; }

        public string Email { get; set; } = string.Empty;

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }

    [DbContext(typeof(DropIndexContext))]
    [Migration("20261107000100_DropForeignIndex")]
    public sealed class DropForeignIndexMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropIndex("core_shared_index", TableName);

        protected override void Down(MigrationBuilder migrationBuilder) { }

        protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
    }

    [DbContext(typeof(RenameIndexContext))]
    [Migration("20261107000200_RenameForeignIndex")]
    public sealed class RenameForeignIndexMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.RenameIndex("core_shared_index", TableName, $"{TablePrefix}ix_renamed_index");

        protected override void Down(MigrationBuilder migrationBuilder) { }

        protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
    }

    [DbContext(typeof(CreateIndexContext))]
    [Migration("20261107000300_CreateForeignIndex")]
    public sealed class CreateForeignIndexMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.CreateIndex("core_shared_index", TableName, "email");

        protected override void Down(MigrationBuilder migrationBuilder) { }

        protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
    }

    public sealed class ViewMappedContext : ExtensionDbContext
    {
        public ViewMappedContext(
            DbContextOptions<ViewMappedContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LeakedRecord>(entity =>
            {
                entity.HasKey(record => record.Id);
                entity.ToView("core_private_record");
            });
    }

    public sealed class SqlQueryMappedContext : ExtensionDbContext
    {
        public SqlQueryMappedContext(
            DbContextOptions<SqlQueryMappedContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LeakedRecord>(entity =>
            {
                entity.HasKey(record => record.Id);
                entity.ToSqlQuery(
                    "SELECT id, value, create_by, create_time, update_by, update_time FROM core_private_record"
                );
            });
    }

    public sealed class LeakedRecord : IExtensionOwnedEntity
    {
        public int Id { get; set; }

        public string Value { get; set; } = string.Empty;

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }

    public sealed class NavigationContext : ExtensionDbContext
    {
        public NavigationContext(
            DbContextOptions<NavigationContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        public DbSet<NavigationRoot> Roots => Set<NavigationRoot>();

        public DbSet<NavigationNote> Notes => Set<NavigationNote>();

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NavigationRoot>(entity =>
            {
                entity.ToTable("navigation_root");
                entity.HasKey(root => root.Id);
                entity.Property(root => root.Id).HasColumnName("id");
                entity.Property(root => root.Name).HasColumnName("name");
                entity.Property(root => root.CreateBy).HasColumnName("create_by");
                entity.Property(root => root.CreateTime).HasColumnName("create_time");
                entity.Property(root => root.UpdateBy).HasColumnName("update_by");
                entity.Property(root => root.UpdateTime).HasColumnName("update_time");
            });
            modelBuilder.Entity<NavigationNote>(entity =>
            {
                entity.ToTable("navigation_note");
                entity.HasKey(note => note.Id);
                entity.Property(note => note.Id).HasColumnName("id");
                entity.Property(note => note.RootId).HasColumnName("root_id");
                entity.Property(note => note.Value).HasColumnName("value");
                entity.Property(note => note.CreateBy).HasColumnName("create_by");
                entity.Property(note => note.CreateTime).HasColumnName("create_time");
                entity.Property(note => note.UpdateBy).HasColumnName("update_by");
                entity.Property(note => note.UpdateTime).HasColumnName("update_time");
                entity.HasOne(note => note.Root).WithMany(root => root.Notes).HasForeignKey(note => note.RootId);
            });
        }
    }

    public sealed class NavigationRoot : IExtensionOwnedEntity
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<NavigationNote> Notes { get; set; } = [];

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }

    public sealed class NavigationNote : IExtensionOwnedChild<NavigationRoot, Guid>
    {
        public Guid Id { get; set; }

        public Guid RootId { get; set; }

        public string Value { get; set; } = string.Empty;

        public NavigationRoot Root { get; set; } = null!;

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }

    public sealed class UnprefixedConstraintContext : ExtensionDbContext
    {
        public UnprefixedConstraintContext(
            DbContextOptions<UnprefixedConstraintContext> options,
            IExtensionDatabaseIdentity identity,
            IExtensionUserContext userContext,
            TimeProvider timeProvider
        )
            : base(options, identity, userContext, timeProvider) { }

        protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UnprefixedConstraintRecord>(entity =>
            {
                entity.ToTable("unprefixed_constraint_record");
                entity.HasKey(record => record.Id).HasName("shared_primary");
                entity.HasAlternateKey(record => record.Name).HasName("shared_unique");
            });
        }
    }

    public sealed class UnprefixedConstraintRecord : IExtensionOwnedEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? CreateBy { get; set; }

        public DateTimeOffset CreateTime { get; set; }

        public string? UpdateBy { get; set; }

        public DateTimeOffset? UpdateTime { get; set; }
    }
}
