using Agw.Auth.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntityCreatorInterceptor : SaveChangesInterceptor
{
    private readonly IEntityAuditUserIdProvider _entityAuditUserIdProvider;
    private readonly TimeProvider _timeProvider;

    public EntityCreatorInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
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
            if (entry.State == EntityState.Added)
            {
                EntityAuditStamping.StampCreated(entry, userId, now);
                EnsureOwnerMatchesCurrentUser(entry);
            }
        }
    }

    private static void EnsureOwnerMatchesCurrentUser(EntityEntry entry)
    {
        if (!UserInfoUtil.IsContextActive || UserInfoUtil.IsSystemScopeActive || entry.Entity is JobLog)
        {
            return;
        }

        var currentUserId = UserInfoUtil.RequiredUserId;
        foreach (var propertyName in new[] { nameof(IEntityCreator.CreateBy), "UserId" })
        {
            var property = entry.Metadata.FindProperty(propertyName);
            if (property == null)
            {
                continue;
            }

            var value = entry.Property(propertyName).CurrentValue as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (propertyName == "UserId")
                {
                    entry.Property(propertyName).CurrentValue = currentUserId;
                }

                continue;
            }

            if (!string.Equals(value.Trim(), currentUserId, StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"{propertyName} must match the current user.");
            }
        }
    }
}
