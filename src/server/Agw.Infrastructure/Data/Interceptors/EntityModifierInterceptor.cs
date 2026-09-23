using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Settings;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntityModifierInterceptor : EntityAuditInterceptorBase
{
    public EntityModifierInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
        : base(entityAuditUserIdProvider, timeProvider) { }

    protected override void OnSavingEntry(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.State == EntityState.Modified)
        {
            EnsureCreateByUnchanged(entry);
            EntityOwnerGuard.EnsureOwnerMatchesCurrentUser(entry, EntityOwnerStage.Modified);
            EntityAuditStamping.StampModified(entry, userId, now);
        }
    }

    private static void EnsureCreateByUnchanged(EntityEntry entry)
    {
        if (entry.Entity is Setting)
        {
            foreach (var name in new[] { nameof(Setting.CreateTime), nameof(Setting.UserId), nameof(Setting.Key) })
            {
                var field = entry.Property(name);
                if (field.IsModified && !Equals(field.OriginalValue, field.CurrentValue))
                    throw new AgwException(ErrorCodes.InvalidParam, $"{name} is immutable.");
            }
        }
        var property = entry.Metadata.FindProperty(nameof(IEntityCreator.CreateBy));
        if (
            property != null
            && entry.Property(property.Name).IsModified
            && !string.Equals(
                entry.Property(property.Name).OriginalValue as string,
                entry.Property(property.Name).CurrentValue as string,
                StringComparison.Ordinal
            )
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "CreateBy is immutable.");
        }
    }
}
