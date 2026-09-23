using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntitySoftDeleteInterceptor : EntityAuditInterceptorBase
{
    public EntitySoftDeleteInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
        : base(entityAuditUserIdProvider, timeProvider) { }

    protected override void OnSavingEntry(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.State == EntityState.Deleted)
        {
            if (entry.Entity is ISoftDelete entity)
            {
                entity.IsDeleted = true;
                EntityAuditStamping.StampDeleted(entry, userId, now);

                entry.State = EntityState.Modified;
            }
        }
    }
}
