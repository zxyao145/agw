using Agw.Extensions.Sdk.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionSqliteMigrationTests
{
    private const string TablePrefix = "zext_0123456789abcdef0123456789abcdef_";
    private const string TableName = $"{TablePrefix}calendar_entry";

    [Fact]
    public async Task AlterColumn_RebuildsTableInsideExtensionNamespace()
    {
        string temporaryName = $"{TablePrefix}ef_temp_calendar_entry";
        var services = new ServiceCollection();
        services.AddEntityFrameworkSqlite();
        Type providerSqlGenerationHelperType = services
            .Last(descriptor => descriptor.ServiceType == typeof(ISqlGenerationHelper))
            .ImplementationType!;
        services.Replace(
            ServiceDescriptor.Singleton<ISqlGenerationHelper>(serviceProvider => new ExtensionSqlGenerationHelper(
                serviceProvider.GetRequiredService<RelationalSqlGenerationHelperDependencies>(),
                TablePrefix,
                (ISqlGenerationHelper)
                    ActivatorUtilities.CreateInstance(serviceProvider, providerSqlGenerationHelperType)
            ))
        );
        services.AddDbContext<SqlGenerationContext>(
            (serviceProvider, options) =>
                options.UseSqlite("Data Source=:memory:").UseInternalServiceProvider(serviceProvider)
        );
        await using var serviceProvider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        SqlGenerationContext context = scope.ServiceProvider.GetRequiredService<SqlGenerationContext>();
        ISqlGenerationHelper sqlGenerationHelper = context.Database.GetService<ISqlGenerationHelper>();
        IModel model = context.GetService<IDesignTimeModel>().Model;
        var builder = new System.Text.StringBuilder();
        sqlGenerationHelper.DelimitIdentifier(builder, $"ef_temp_{TableName}");
        Assert.Equal($"\"{temporaryName}\"", builder.ToString());
        Assert.Equal($"\"{temporaryName}\"", sqlGenerationHelper.DelimitIdentifier($"ef_temp_{TableName}"));
        Assert.Equal($"\"{temporaryName}\"", sqlGenerationHelper.DelimitIdentifier($"ef_temp_{TableName}", null));
        builder.Clear();
        sqlGenerationHelper.DelimitIdentifier(builder, $"ef_temp_{TableName}", null);
        Assert.Equal($"\"{temporaryName}\"", builder.ToString());

        IMigrationsSqlGenerator generator = context.Database.GetService<IMigrationsSqlGenerator>();
        IReadOnlyList<MigrationCommand> createCommands = generator.Generate(
            [
                new CreateTableOperation
                {
                    Name = TableName,
                    Columns =
                    {
                        new AddColumnOperation
                        {
                            Name = "id",
                            ClrType = typeof(int),
                            ColumnType = "INTEGER",
                            IsNullable = false,
                        },
                        new AddColumnOperation
                        {
                            Name = "title",
                            ClrType = typeof(string),
                            ColumnType = "TEXT",
                            IsNullable = false,
                        },
                    },
                    PrimaryKey = new AddPrimaryKeyOperation { Name = $"pk_{TableName}", Columns = ["id"] },
                },
            ],
            model,
            MigrationsSqlGenerationOptions.Default
        );
        var oldColumn = new AddColumnOperation
        {
            Name = "title",
            Table = TableName,
            ClrType = typeof(string),
            ColumnType = "TEXT",
            IsNullable = false,
        };
        IReadOnlyList<MigrationCommand> alterCommands = generator.Generate(
            [
                new AlterColumnOperation
                {
                    Name = "title",
                    Table = TableName,
                    ClrType = typeof(string),
                    ColumnType = "TEXT",
                    IsNullable = true,
                    OldColumn = oldColumn,
                },
            ],
            model,
            MigrationsSqlGenerationOptions.Default
        );

        Assert.Contains(
            alterCommands,
            command => command.CommandText.Contains($"\"{temporaryName}\"", StringComparison.Ordinal)
        );

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, createCommands);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO \"{TableName}\" (\"id\", \"title\") VALUES (1, 'kept')";
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(connection, alterCommands);

        await using var verify = connection.CreateCommand();
        verify.CommandText = $"SELECT \"title\" FROM \"{TableName}\" WHERE \"id\" = 1";
        Assert.Equal("kept", await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var tableQuery = connection.CreateCommand();
        tableQuery.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await tableQuery.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.StartsWith(TablePrefix, reader.GetString(0), StringComparison.Ordinal);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, IReadOnlyList<MigrationCommand> commands)
    {
        foreach (MigrationCommand migrationCommand in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = migrationCommand.CommandText;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class SqlGenerationContext(DbContextOptions<SqlGenerationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SqlGenerationRow>(entity =>
            {
                entity.ToTable(TableName);
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id).HasColumnName("id");
                entity.Property(row => row.Title).HasColumnName("title").IsRequired(false);
            });
        }
    }

    private sealed class SqlGenerationRow
    {
        public int Id { get; set; }

        public string? Title { get; set; }
    }
}
