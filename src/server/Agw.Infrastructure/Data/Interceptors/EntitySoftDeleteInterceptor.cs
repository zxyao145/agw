using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntitySoftDeleteInterceptor : SaveChangesInterceptor
{
    private readonly IEntityAuditUserIdProvider _entityAuditUserIdProvider;
    private readonly TimeProvider _timeProvider;

    public EntitySoftDeleteInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
    {
        _entityAuditUserIdProvider = entityAuditUserIdProvider;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        BeforeSaveChanges(eventData, _entityAuditUserIdProvider.GetUserId(), _timeProvider.GetUtcNow());
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = new CancellationToken()
    )
    {
        BeforeSaveChanges(eventData, _entityAuditUserIdProvider.GetUserId(), _timeProvider.GetUtcNow());
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void BeforeSaveChanges(DbContextEventData eventData, string userId, DateTimeOffset now)
    {
        if (eventData.Context is null)
        {
            return;
        }
        foreach (var entry in eventData.Context.ChangeTracker.Entries())
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
}
