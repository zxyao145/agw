using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Interceptors;

internal static class EntityAuditStamping
{
    public static void StampCreated(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.Entity is IEntityCreator entity)
        {
            if (string.IsNullOrWhiteSpace(entity.CreateBy))
            {
                entity.CreateBy = userId;
            }

            if (entity.CreateTime == default)
            {
                entity.CreateTime = now;
            }
        }
        else if (entry.Entity is IEntityCreateTime entity2 && entity2.CreateTime == default)
        {
            entity2.CreateTime = now;
        }
    }

    public static void StampModified(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.Entity is IEntityModifier entity)
        {
            if (!IsExplicitlyModified(entry, nameof(IEntityModifier.UpdateBy))
                || string.IsNullOrWhiteSpace(entity.UpdateBy))
            {
                entity.UpdateBy = userId;
                MarkModified(entry, nameof(IEntityModifier.UpdateBy));
            }

            if (!IsExplicitlyModified(entry, nameof(IEntityModifier.UpdateTime))
                || !entity.UpdateTime.HasValue)
            {
                entity.UpdateTime = now;
                MarkModified(entry, nameof(IEntityModifier.UpdateTime));
            }
        }
        else if (entry.Entity is IEntityModifyTime entity2
                 && (!IsExplicitlyModified(entry, nameof(IEntityModifyTime.UpdateTime))
                     || !entity2.UpdateTime.HasValue))
        {
            entity2.UpdateTime = now;
            MarkModified(entry, nameof(IEntityModifyTime.UpdateTime));
        }
    }

    public static void StampDeleted(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.Entity is ISoftDeleteAudit entity)
        {
            if (string.IsNullOrWhiteSpace(entity.DeleteBy))
            {
                entity.DeleteBy = userId;
            }

            if (!entity.DeletionTime.HasValue)
            {
                entity.DeletionTime = now;
            }
        }
    }

    private static bool IsExplicitlyModified(EntityEntry entry, string propertyName)
    {
        return entry.Metadata.FindProperty(propertyName) is not null
            && entry.Property(propertyName).IsModified;
    }

    private static void MarkModified(EntityEntry entry, string propertyName)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).IsModified = true;
        }
    }
}
