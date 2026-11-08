using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal static class ExtensionMigrationValidator
{
    private const string NpgsqlIndexMethodAnnotation = "Npgsql:IndexMethod";

    public static void Validate(DbContext context, ExtensionDatabaseSchemaMetadata options)
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var provider = context.Database.ProviderName;
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid("The extension database provider is unavailable.");
        }

        var maximumIdentifierBytes = context.Model.GetMaxIdentifierLength();
        var typeMappingSource = context.GetService<IRelationalTypeMappingSource>();
        foreach (var migrationClass in migrationsAssembly.Migrations.Values)
        {
            var migration = migrationsAssembly.CreateMigration(migrationClass, provider);
            ValidateOperations(migration.UpOperations, options, maximumIdentifierBytes, typeMappingSource);
        }
    }

    internal static void ValidateOperations(
        IReadOnlyList<MigrationOperation> operations,
        ExtensionDatabaseSchemaMetadata options,
        int maximumIdentifierBytes,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case SqlOperation:
                case AddForeignKeyOperation:
                case DropForeignKeyOperation:
                case AddCheckConstraintOperation:
                case CreateSequenceOperation:
                case DropSequenceOperation:
                case AlterSequenceOperation:
                case RestartSequenceOperation:
                case RenameSequenceOperation:
                case EnsureSchemaOperation:
                case DropSchemaOperation:
                case AlterDatabaseOperation:
                    throw ExtensionDatabaseModelValidator.MigrationInvalid(
                        "The extension migration contains an unsupported operation."
                    );
                case CreateTableOperation createTable:
                    ValidateTable(createTable.Name, createTable.Schema, options, maximumIdentifierBytes);
                    ValidateCreateTable(createTable, maximumIdentifierBytes, options, typeMappingSource);
                    break;
                case RenameTableOperation renameTable:
                    ValidateTable(renameTable.Name, renameTable.Schema, options, maximumIdentifierBytes);
                    ValidateTable(renameTable.NewName, renameTable.NewSchema, options, maximumIdentifierBytes);
                    break;
                case AddColumnOperation addColumn:
                    ValidateTableOperation(addColumn, options, maximumIdentifierBytes);
                    ValidateColumn(addColumn, maximumIdentifierBytes, typeMappingSource);
                    break;
                case AlterColumnOperation alterColumn:
                    ValidateTableOperation(alterColumn, options, maximumIdentifierBytes);
                    ValidateColumn(alterColumn, maximumIdentifierBytes, typeMappingSource);
                    break;
                case DropColumnOperation dropColumn:
                    ValidateTableOperation(dropColumn, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateIdentifier(dropColumn.Name, maximumIdentifierBytes);
                    break;
                case RenameColumnOperation renameColumn:
                    ValidateTableOperation(renameColumn, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateIdentifier(renameColumn.Name, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateIdentifier(renameColumn.NewName, maximumIdentifierBytes);
                    break;
                case CreateIndexOperation createIndex:
                    ValidateTableOperation(createIndex, options, maximumIdentifierBytes);
                    ValidateIndex(
                        createIndex.Name,
                        createIndex.Columns,
                        createIndex.Filter,
                        createIndex[NpgsqlIndexMethodAnnotation],
                        options,
                        maximumIdentifierBytes
                    );
                    break;
                case DropIndexOperation dropIndex:
                    ValidateTableName(dropIndex.Table, dropIndex.Schema, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateIndexName(
                        dropIndex.Name,
                        options.TablePrefix,
                        maximumIdentifierBytes
                    );
                    break;
                case RenameIndexOperation renameIndex:
                    ValidateTableName(renameIndex.Table, renameIndex.Schema, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateIndexName(
                        renameIndex.Name,
                        options.TablePrefix,
                        maximumIdentifierBytes
                    );
                    ExtensionDatabaseModelValidator.ValidateIndexName(
                        renameIndex.NewName,
                        options.TablePrefix,
                        maximumIdentifierBytes
                    );
                    break;
                case InsertDataOperation insertData:
                    ValidateTableOperation(insertData, options, maximumIdentifierBytes);
                    ValidateColumns(insertData.Columns, maximumIdentifierBytes);
                    break;
                case UpdateDataOperation updateData:
                    ValidateTableOperation(updateData, options, maximumIdentifierBytes);
                    ValidateColumns(updateData.Columns, maximumIdentifierBytes);
                    ValidateColumns(updateData.KeyColumns, maximumIdentifierBytes);
                    break;
                case DeleteDataOperation deleteData:
                    ValidateTableOperation(deleteData, options, maximumIdentifierBytes);
                    ValidateColumns(deleteData.KeyColumns, maximumIdentifierBytes);
                    break;
                case AddPrimaryKeyOperation addPrimaryKey:
                    ValidateTableOperation(addPrimaryKey, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateConstraintName(
                        addPrimaryKey.Name,
                        options.TablePrefix,
                        isPrimaryKey: true,
                        maximumIdentifierBytes
                    );
                    ValidateColumns(addPrimaryKey.Columns, maximumIdentifierBytes);
                    break;
                case DropPrimaryKeyOperation dropPrimaryKey:
                    ValidateTableOperation(dropPrimaryKey, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateConstraintName(
                        dropPrimaryKey.Name,
                        options.TablePrefix,
                        isPrimaryKey: true,
                        maximumIdentifierBytes
                    );
                    break;
                case AddUniqueConstraintOperation addUniqueConstraint:
                    ValidateTableOperation(addUniqueConstraint, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateConstraintName(
                        addUniqueConstraint.Name,
                        options.TablePrefix,
                        isPrimaryKey: false,
                        maximumIdentifierBytes
                    );
                    ValidateColumns(addUniqueConstraint.Columns, maximumIdentifierBytes);
                    break;
                case DropUniqueConstraintOperation dropUniqueConstraint:
                    ValidateTableOperation(dropUniqueConstraint, options, maximumIdentifierBytes);
                    ExtensionDatabaseModelValidator.ValidateConstraintName(
                        dropUniqueConstraint.Name,
                        options.TablePrefix,
                        isPrimaryKey: false,
                        maximumIdentifierBytes
                    );
                    break;
                case ITableMigrationOperation tableOperation:
                    ValidateTableOperation(tableOperation, options, maximumIdentifierBytes);
                    break;
                default:
                    throw ExtensionDatabaseModelValidator.MigrationInvalid(
                        "The extension migration contains an unsupported operation."
                    );
            }
        }
    }

    private static void ValidateCreateTable(
        CreateTableOperation operation,
        int maximumIdentifierBytes,
        ExtensionDatabaseSchemaMetadata options,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        if (operation.ForeignKeys.Count > 0 || operation.CheckConstraints.Count > 0)
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid(
                "The extension migration contains a foreign key or SQL check constraint."
            );
        }

        foreach (var column in operation.Columns)
        {
            ValidateColumn(column, maximumIdentifierBytes, typeMappingSource);
        }

        if (operation.PrimaryKey is not null)
        {
            ExtensionDatabaseModelValidator.ValidateConstraintName(
                operation.PrimaryKey.Name,
                options.TablePrefix,
                isPrimaryKey: true,
                maximumIdentifierBytes
            );
            ValidateColumns(operation.PrimaryKey.Columns, maximumIdentifierBytes);
        }

        foreach (var constraint in operation.UniqueConstraints)
        {
            ExtensionDatabaseModelValidator.ValidateConstraintName(
                constraint.Name,
                options.TablePrefix,
                isPrimaryKey: false,
                maximumIdentifierBytes
            );
            ValidateColumns(constraint.Columns, maximumIdentifierBytes);
        }
    }

    private static void ValidateColumn(
        ColumnOperation operation,
        int maximumIdentifierBytes,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ExtensionDatabaseModelValidator.ValidateIdentifier(operation.Name, maximumIdentifierBytes);
        if (operation.DefaultValueSql is not null || operation.ComputedColumnSql is not null)
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid(
                "The extension migration contains an SQL column expression."
            );
        }

        if (operation.Collation is not null)
        {
            ExtensionDatabaseModelValidator.ValidateCollation(operation.Collation);
        }

        var typeMapping = typeMappingSource.FindMapping(
            operation.ClrType,
            storeTypeName: null,
            keyOrIndex: false,
            unicode: operation.IsUnicode,
            size: operation.MaxLength,
            rowVersion: operation.IsRowVersion,
            fixedLength: operation.IsFixedLength,
            precision: operation.Precision,
            scale: operation.Scale
        );
        if (
            typeMapping is null
            || (
                operation.ColumnType is not null
                && !string.Equals(typeMapping.StoreType, operation.ColumnType, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid(
                "The extension migration contains an unsupported column type."
            );
        }
    }

    private static void ValidateIndex(
        string name,
        IReadOnlyList<string> columns,
        string? filter,
        object? indexMethod,
        ExtensionDatabaseSchemaMetadata options,
        int maximumIdentifierBytes
    )
    {
        ExtensionDatabaseModelValidator.ValidateIndexName(name, options.TablePrefix, maximumIdentifierBytes);
        ValidateColumns(columns, maximumIdentifierBytes);
        if (filter is not null)
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid(
                "The extension migration contains an SQL index filter."
            );
        }

        if (indexMethod is not null && (indexMethod is not string method || !IsValidIndexMethod(method)))
        {
            throw ExtensionDatabaseModelValidator.MigrationInvalid(
                "The extension migration contains an invalid PostgreSQL index method."
            );
        }
    }

    private static bool IsValidIndexMethod(string method) =>
        method.Length > 0
        && (char.IsAsciiLetter(method[0]) || method[0] == '_')
        && method.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static void ValidateTableOperation(
        ITableMigrationOperation operation,
        ExtensionDatabaseSchemaMetadata options,
        int maximumIdentifierBytes
    )
    {
        ValidateTableName(operation.Table, operation.Schema, options, maximumIdentifierBytes);
    }

    private static void ValidateTable(
        string? name,
        string? schema,
        ExtensionDatabaseSchemaMetadata options,
        int maximumIdentifierBytes
    )
    {
        ValidateTableName(name, schema, options, maximumIdentifierBytes);
    }

    private static void ValidateTableName(
        string? name,
        string? schema,
        ExtensionDatabaseSchemaMetadata options,
        int maximumIdentifierBytes
    )
    {
        ExtensionDatabaseModelValidator.ValidateTableName(name, schema, options.TablePrefix, maximumIdentifierBytes);
        if (options.Provider == ExtensionDatabaseProvider.Sqlite)
        {
            ExtensionDatabaseModelValidator.ValidateIdentifier(
                $"{options.TablePrefix}ef_temp_{name![options.TablePrefix.Length..]}",
                maximumIdentifierBytes
            );
        }
    }

    private static void ValidateColumns(IEnumerable<string> columns, int maximumIdentifierBytes)
    {
        foreach (var column in columns)
        {
            ExtensionDatabaseModelValidator.ValidateIdentifier(column, maximumIdentifierBytes);
        }
    }
}
