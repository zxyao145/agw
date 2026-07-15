using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Encryption;

internal sealed class EncryptedPropertyProcessor
{
    private readonly IEncryptedDataProtector _protector;

    public EncryptedPropertyProcessor(IEncryptedDataProtector protector)
    {
        _protector = protector;
    }

    public IReadOnlyList<EncryptedPropertyRestore> EncryptPendingChanges(ChangeTracker changeTracker)
    {
        var restores = new List<EncryptedPropertyRestore>();
        try
        {
            foreach (var entry in changeTracker.Entries()
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                var encryptedProperties = EncryptedEntityMetadata.GetEncryptedProperties(entry.Metadata).ToList();
                if (encryptedProperties.Count == 0)
                {
                    continue;
                }

                var (tableName, entityId) = EncryptedEntityMetadata.GetIdentity(entry.Metadata, entry.Entity);
                foreach (var property in encryptedProperties)
                {
                    var propertyEntry = entry.Property(property.Name);
                    if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
                    {
                        continue;
                    }

                    var plaintext = propertyEntry.CurrentValue;
                    restores.Add(new EncryptedPropertyRestore(propertyEntry, plaintext));
                    propertyEntry.CurrentValue = EncryptValue(tableName, entityId, plaintext);
                }
            }

            return restores;
        }
        catch
        {
            RestorePlaintext(restores);
            throw;
        }
    }

    public void RestorePlaintext(IEnumerable<EncryptedPropertyRestore> restores)
    {
        foreach (var restore in restores)
        {
            restore.PropertyEntry.CurrentValue = restore.Plaintext;
        }
    }

    public void DecryptMaterializedEntity(DbContext context, object entity)
    {
        var entityType = context.Model.FindEntityType(entity.GetType());
        if (entityType == null)
        {
            return;
        }

        var encryptedProperties = EncryptedEntityMetadata.GetEncryptedProperties(entityType).ToList();
        if (encryptedProperties.Count == 0)
        {
            return;
        }

        var (tableName, entityId) = EncryptedEntityMetadata.GetIdentity(entityType, entity);
        foreach (var property in encryptedProperties)
        {
            var propertyInfo = property.PropertyInfo!;
            var protectedValue = propertyInfo.GetValue(entity);
            propertyInfo.SetValue(entity, DecryptValue(tableName, entityId, protectedValue));
        }
    }

    private object? EncryptValue(string tableName, Guid entityId, object? value) => value switch
    {
        null => null,
        string plaintext => _protector.Protect(tableName, entityId, plaintext),
        Dictionary<string, string> values => values.ToDictionary(
            pair => pair.Key,
            pair => _protector.Protect(tableName, entityId, pair.Value),
            values.Comparer),
        _ => throw new AgwException(
            ErrorCodes.EncryptedModelInvalid,
            $"Encrypted value type '{value.GetType().Name}' is not supported.")
    };

    private object? DecryptValue(string tableName, Guid entityId, object? value) => value switch
    {
        null => null,
        string protectedValue => _protector.Unprotect(tableName, entityId, protectedValue),
        Dictionary<string, string> values => values.ToDictionary(
            pair => pair.Key,
            pair => _protector.Unprotect(tableName, entityId, pair.Value),
            values.Comparer),
        _ => throw new AgwException(
            ErrorCodes.EncryptedModelInvalid,
            $"Encrypted value type '{value.GetType().Name}' is not supported.")
    };
}

internal sealed record EncryptedPropertyRestore(PropertyEntry PropertyEntry, object? Plaintext);
