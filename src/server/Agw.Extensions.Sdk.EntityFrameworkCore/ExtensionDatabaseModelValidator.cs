using System.Text;
using System.Text.RegularExpressions;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal static partial class ExtensionDatabaseModelValidator
{
    private static readonly Regex SnakeCaseIdentifier = new(
        "^[a-z0-9]+(?:_[a-z0-9]+)*$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex DatabaseIdentifier = new(
        "^[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex CollationIdentifier = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    public static void Validate(
        IModel model,
        ExtensionDatabaseSchemaMetadata options,
        IRelationalTypeMappingSource typeMappingSource,
        bool requireOwnership = true
    )
    {
        var maximumIdentifierBytes = model.GetMaxIdentifierLength();
        var relationalModel = model.GetRelationalModel();
        if (
            relationalModel.Views.Any()
            || relationalModel.Queries.Any()
            || relationalModel.Functions.Any()
            || relationalModel.StoredProcedures.Any()
            || relationalModel.Sequences.Any()
        )
        {
            throw MigrationInvalid("Extension database models may map only extension-owned tables.");
        }

        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetTableName() is null)
            {
                throw MigrationInvalid("Every extension database entity must map to an extension-owned table.");
            }

            if (requireOwnership && !typeof(IExtensionOwnedEntity).IsAssignableFrom(entityType.ClrType))
            {
                throw MigrationInvalid("Every extension table entity must declare user ownership.");
            }
        }

        foreach (var table in relationalModel.Tables)
        {
            ValidateTableName(table.Name, table.Schema, options.TablePrefix, maximumIdentifierBytes);
            if (table.CheckConstraints.Any())
            {
                throw MigrationInvalid("Extension database tables cannot contain SQL check constraints.");
            }
            if (options.Provider == ExtensionDatabaseProvider.Sqlite)
            {
                ValidateIdentifier(
                    $"{options.TablePrefix}ef_temp_{table.Name[options.TablePrefix.Length..]}",
                    maximumIdentifierBytes
                );
            }
            foreach (var column in table.Columns)
            {
                ValidateIdentifier(column.Name, maximumIdentifierBytes);
                if (column.DefaultValueSql is not null || column.ComputedColumnSql is not null)
                {
                    throw MigrationInvalid("Extension database columns cannot contain SQL expressions.");
                }
                if (column.Collation is not null)
                {
                    ValidateCollation(column.Collation);
                }
                ValidateColumnType(column, typeMappingSource);
            }

            foreach (var index in table.Indexes)
            {
                ValidateIndexName(index.Name, options.TablePrefix, maximumIdentifierBytes);
                if (index.Filter is not null)
                    throw MigrationInvalid("Extension database indexes cannot contain SQL filters.");
            }

            foreach (var constraint in table.UniqueConstraints)
            {
                ValidateConstraintName(
                    constraint.Name,
                    options.TablePrefix,
                    constraint.GetIsPrimaryKey(),
                    maximumIdentifierBytes
                );
            }
        }
    }

    internal static void ValidateTableName(string? name, string? schema, string prefix, int maximumIdentifierBytes)
    {
        if (
            string.IsNullOrEmpty(name)
            || !string.IsNullOrEmpty(schema)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
        )
        {
            throw MigrationInvalid("An extension table is outside its assigned table namespace.");
        }

        var shortName = name[prefix.Length..];
        if (
            shortName.StartsWith("__", StringComparison.Ordinal)
            || shortName.StartsWith("zext_", StringComparison.Ordinal)
        )
        {
            throw MigrationInvalid("The extension table short name is reserved.");
        }

        ValidateIdentifier(shortName, maximumIdentifierBytes - Encoding.UTF8.GetByteCount(prefix));
        ValidateIdentifier(name, maximumIdentifierBytes);
    }

    internal static void ValidateIdentifier(string? name, int maximumIdentifierBytes)
    {
        if (
            string.IsNullOrEmpty(name)
            || !SnakeCaseIdentifier.IsMatch(name)
            || Encoding.UTF8.GetByteCount(name) > maximumIdentifierBytes
        )
        {
            throw MigrationInvalid(
                $"The extension database identifier '{name}' is invalid for the configured provider."
            );
        }
    }

    internal static void ValidateDatabaseIdentifier(string? name, int maximumIdentifierBytes)
    {
        if (
            string.IsNullOrEmpty(name)
            || !DatabaseIdentifier.IsMatch(name)
            || Encoding.UTF8.GetByteCount(name) > maximumIdentifierBytes
        )
        {
            throw MigrationInvalid(
                $"The extension database identifier '{name}' is invalid for the configured provider."
            );
        }
    }

    internal static void ValidateConstraintName(
        string? name,
        string tablePrefix,
        bool isPrimaryKey,
        int maximumIdentifierBytes
    )
    {
        ValidateDatabaseIdentifier(name, maximumIdentifierBytes);
        if (
            name is null
            || (
                !name.StartsWith(tablePrefix, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith($"{(isPrimaryKey ? "pk" : "ak")}_{tablePrefix}", StringComparison.OrdinalIgnoreCase)
                && (isPrimaryKey || !name.StartsWith($"uq_{tablePrefix}", StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            throw MigrationInvalid("An extension key constraint is outside its assigned namespace.");
        }
    }

    internal static void ValidateCollation(string collation)
    {
        if (!CollationIdentifier.IsMatch(collation))
        {
            throw MigrationInvalid("An extension database collation name is invalid.");
        }
    }

    internal static void ValidateIndexName(string? name, string tablePrefix, int maximumIdentifierBytes)
    {
        ValidateDatabaseIdentifier(name, maximumIdentifierBytes);
        if (
            name is null
            || (
                !name.StartsWith(tablePrefix, StringComparison.Ordinal)
                && !name.StartsWith($"ix_{tablePrefix}", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw MigrationInvalid("An extension index is outside its assigned index namespace.");
        }
    }

    private static void ValidateColumnType(IColumn column, IRelationalTypeMappingSource typeMappingSource)
    {
        var mappings = column.PropertyMappings;
        if (mappings.Count == 0)
        {
            throw MigrationInvalid("An extension database column has no supported type mapping.");
        }

        foreach (var mapping in mappings)
        {
            var property = mapping.Property;
            var storeObject = StoreObjectIdentifier.Table(column.Table.Name, column.Table.Schema);
            Type clrType = property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
            var typeMapping = typeMappingSource.FindMapping(
                clrType,
                storeTypeName: null,
                keyOrIndex: false,
                unicode: property.IsUnicode(storeObject),
                size: property.GetMaxLength(storeObject),
                rowVersion: property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate,
                fixedLength: property.IsFixedLength(storeObject),
                precision: property.GetPrecision(storeObject),
                scale: property.GetScale(storeObject)
            );
            if (
                typeMapping is null
                || !string.Equals(typeMapping.StoreType, column.StoreType, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw MigrationInvalid(
                    $"Extension column '{column.Name}' uses unsupported type '{column.StoreType}' (expected '{typeMapping?.StoreType}')."
                );
            }
        }
    }

    internal static AgwException MigrationInvalid(string message) => new(ErrorCodes.ExtensionMigrationFailed, message);
}
