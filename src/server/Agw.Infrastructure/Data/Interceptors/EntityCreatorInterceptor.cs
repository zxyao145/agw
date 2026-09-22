using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntityCreatorInterceptor : EntityAuditInterceptorBase
{
    public EntityCreatorInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
        : base(entityAuditUserIdProvider, timeProvider) { }

    protected override void OnSavingEntry(EntityEntry entry, string userId, DateTimeOffset now)
    {
        if (entry.State == EntityState.Added)
        {
            EntityAuditStamping.StampCreated(entry, userId, now);
            EntityOwnerGuard.EnsureOwnerMatchesCurrentUser(entry, EntityOwnerStage.Created);
        }
    }
}
