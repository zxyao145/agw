using System.Globalization;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed partial class ExtensionDatabaseMigrationTests
{
    [Theory]
    [InlineData("event_source", false)]
    [InlineData("zext_00000000000000000000000000000001_record", false)]
    [InlineData("__ef_migrations_history", true)]
    [InlineData("__inbox", true)]
    [InlineData("__outbox", true)]
    public void ValidateOperations_UpdateAllRowsOutsideBusinessTables_ThrowsMigrationFailed(string name, bool prefixed)
    {
        var operation = new ExtensionUpdateAllRowsOperation
        {
            Table = prefixed ? TablePrefix + name : name,
            Assignments = [new("title", "updated")],
        };

        var exception = Assert.Throws<AgwException>(() => ValidateUpdateOperation(operation));

        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }

    [Fact]
    public void ValidateOperations_UpdateAllRowsWithUniqueAssignments_AllowsOwnedBusinessTable()
    {
        ValidateUpdateOperation(
            new ExtensionUpdateAllRowsOperation
            {
                Table = TableName,
                Assignments = [new("title", "updated"), new("update_by", null)],
            }
        );
    }

    [Fact]
    public void ValidateOperations_UpdateAllRowsWithInvalidAssignments_ThrowsMigrationFailed()
    {
        var empty = new ExtensionUpdateAllRowsOperation { Table = TableName, Assignments = [] };
        var duplicate = new ExtensionUpdateAllRowsOperation
        {
            Table = TableName,
            Assignments = [new("title", "first"), new("title", "second")],
        };

        Assert.Equal(
            ErrorCodes.ExtensionMigrationFailed.Code,
            Assert.Throws<AgwException>(() => ValidateUpdateOperation(empty)).Code
        );
        Assert.Equal(
            ErrorCodes.ExtensionMigrationFailed.Code,
            Assert.Throws<AgwException>(() => ValidateUpdateOperation(duplicate)).Code
        );
    }

    [Theory]
    [InlineData(ExtensionDatabaseProvider.Sqlite)]
    [InlineData(ExtensionDatabaseProvider.PostgreSql)]
    public void Generate_UpdateAllRows_UsesTargetColumnMappings(ExtensionDatabaseProvider provider)
    {
        var postgres = provider == ExtensionDatabaseProvider.PostgreSql;
        var options = new DbContextOptionsBuilder();
        using var context = new DbContext(
            postgres ? options.UseNpgsql().Options : options.UseSqlite("Data Source=:memory:").Options
        );
        var services = context.GetInfrastructure();
        var metadata = new ExtensionDatabaseSchemaMetadata(provider, TablePrefix);
        IMigrationsSqlGenerator generator = postgres
            ? ActivatorUtilities.CreateInstance<ExtensionNpgsqlMigrationsSqlGenerator>(services, metadata)
            : ActivatorUtilities.CreateInstance<ExtensionSqliteMigrationsSqlGenerator>(services, metadata);
        var model = CreateUpdateTargetModel(context, provider);
        var timestamp = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
        var operation = new ExtensionUpdateAllRowsOperation
        {
            Table = TableName,
            Assignments = [new("title", "owner's title"), new("create_time", timestamp), new("update_by", null)],
        };

        var command = Assert.Single(generator.Generate([operation], model));

        var identifiers = context.GetService<ISqlGenerationHelper>();
        var table = identifiers.DelimitIdentifier(TableName);
        var title = identifiers.DelimitIdentifier("title");
        var created = identifiers.DelimitIdentifier("create_time");
        var updatedBy = identifiers.DelimitIdentifier("update_by");
        Assert.StartsWith($"UPDATE {table} SET {title} = 'owner''s title', ", command.CommandText);
        var timeLiteral = postgres ? "TIMESTAMPTZ" : timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
        Assert.Contains($"{created} = {timeLiteral}", command.CommandText);
        Assert.Contains($"{updatedBy} = NULL", command.CommandText);
        Assert.DoesNotContain(" WHERE ", command.CommandText);
        AssertInvalidAssignment(generator, model, "title", timestamp);
        AssertInvalidAssignment(generator, model, "title", new object());
        AssertInvalidAssignment(generator, model, "missing", "value");
        AssertInvalidAssignment(generator, model, "create_time", null);
        AssertInvalidAssignment(generator, model, "title", "value", TablePrefix + "missing");
    }

    private static IModel CreateUpdateTargetModel(DbContext context, ExtensionDatabaseProvider provider)
    {
        var modelBuilder = new ModelBuilder();
        ConfigureTargetModel(modelBuilder, true, false, context.Database.ProviderName!);
        if (provider == ExtensionDatabaseProvider.Sqlite)
        {
            modelBuilder
                .Entity<MigrationRecord>()
                .Property(record => record.CreateTime)
                .HasColumnType("INTEGER")
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }

        return context.GetService<IModelRuntimeInitializer>().Initialize(modelBuilder.FinalizeModel());
    }

    private static void ValidateUpdateOperation(ExtensionUpdateAllRowsOperation operation)
    {
        using var context = new DbContext(new DbContextOptionsBuilder().UseSqlite("Data Source=:memory:").Options);
        ExtensionMigrationValidator.ValidateOperations(
            [operation],
            new ExtensionDatabaseSchemaMetadata(ExtensionDatabaseProvider.Sqlite, TablePrefix),
            context.Model.GetMaxIdentifierLength(),
            context.GetService<IRelationalTypeMappingSource>()
        );
    }

    private static void AssertInvalidAssignment(
        IMigrationsSqlGenerator generator,
        IModel model,
        string column,
        object? value,
        string? table = null
    )
    {
        var operation = new ExtensionUpdateAllRowsOperation
        {
            Table = table ?? TableName,
            Assignments = [new(column, value)],
        };
        var exception = Assert.Throws<AgwException>(() => generator.Generate([operation], model));
        Assert.Equal(ErrorCodes.ExtensionMigrationFailed.Code, exception.Code);
    }
}
