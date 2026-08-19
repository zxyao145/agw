using System.Reflection;
using Agw.Shared.Data.Encryption;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Agw.Infrastructure.Data.Encryption;

internal static class EncryptedEntityMetadata
{
    public static void Validate(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var encryptedProperties = GetEncryptedProperties(entityType).ToList();
            if (encryptedProperties.Count == 0)
            {
                continue;
            }

            var primaryKey = entityType.FindPrimaryKey();
            if (
                primaryKey == null
                || primaryKey.Properties.Count != 1
                || primaryKey.Properties[0].ClrType != typeof(Guid)
            )
            {
                throw new AgwException(
                    ErrorCodes.EncryptedModelInvalid,
                    $"Entity '{entityType.DisplayName()}' must have a single Guid primary key to use {nameof(EncryptedAttribute)}."
                );
            }

            if (string.IsNullOrWhiteSpace(entityType.GetTableName()))
            {
                throw new AgwException(
                    ErrorCodes.EncryptedModelInvalid,
                    $"Entity '{entityType.DisplayName()}' must map to a table to use {nameof(EncryptedAttribute)}."
                );
            }

            foreach (var property in encryptedProperties)
            {
                if (property.ClrType != typeof(string) && property.ClrType != typeof(Dictionary<string, string>))
                {
                    throw new AgwException(
                        ErrorCodes.EncryptedModelInvalid,
                        $"Encrypted property '{entityType.DisplayName()}.{property.Name}' has unsupported type '{property.ClrType.Name}'."
                    );
                }

                if (property.GetContainingKeys().Any() || property.GetContainingIndexes().Any())
                {
                    throw new AgwException(
                        ErrorCodes.EncryptedModelInvalid,
                        $"Encrypted property '{entityType.DisplayName()}.{property.Name}' cannot participate in a key or index."
                    );
                }
            }
        }
    }

    public static IEnumerable<IProperty> GetEncryptedProperties(IEntityType entityType) =>
        entityType
            .GetProperties()
            .Where(property => property.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() != null);

    private static IEnumerable<IMutableProperty> GetEncryptedProperties(IMutableEntityType entityType) =>
        entityType
            .GetProperties()
            .Where(property => property.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() != null);

    public static (string TableName, Guid EntityId) GetIdentity(IEntityType entityType, object entity)
    {
        var tableName = entityType.GetTableName();
        var primaryKey = entityType.FindPrimaryKey();
        if (
            string.IsNullOrWhiteSpace(tableName)
            || primaryKey == null
            || primaryKey.Properties.Count != 1
            || primaryKey.Properties[0].PropertyInfo == null
            || primaryKey.Properties[0].ClrType != typeof(Guid)
        )
        {
            throw new AgwException(
                ErrorCodes.EncryptedModelInvalid,
                $"Entity '{entityType.DisplayName()}' does not have valid encryption identity metadata."
            );
        }

        var keyProperty = primaryKey.Properties[0].PropertyInfo!;
        var entityId = (Guid)(keyProperty.GetValue(entity) ?? Guid.Empty);
        if (entityId == Guid.Empty)
        {
            throw new AgwException(
                ErrorCodes.EncryptedModelInvalid,
                $"Entity '{entityType.DisplayName()}' must have a non-empty Guid primary key before encrypted data is processed."
            );
        }

        return (tableName, entityId);
    }
}
