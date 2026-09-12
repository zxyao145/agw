using Agw.Auth.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Settings;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Data.Interceptors;

public sealed class EntityModifierInterceptor : SaveChangesInterceptor
{
    private readonly IEntityAuditUserIdProvider _entityAuditUserIdProvider;
    private readonly TimeProvider _timeProvider;

    public EntityModifierInterceptor(IEntityAuditUserIdProvider entityAuditUserIdProvider, TimeProvider timeProvider)
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
            if (entry.State == EntityState.Modified)
            {
                EnsureCreateByUnchanged(entry);
                EnsureOwnerMatchesCurrentUser(entry);
                EntityAuditStamping.StampModified(entry, userId, now);
            }
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

    private static void EnsureOwnerMatchesCurrentUser(EntityEntry entry)
    {
        if (entry.Entity is Setting { UserId: null })
            return;
        if (!UserInfoUtil.IsContextActive || UserInfoUtil.IsSystemScopeActive)
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
            if (propertyName == "UserId" && string.IsNullOrWhiteSpace(value))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "UserId is required.");
            }

            if (
                !string.IsNullOrWhiteSpace(value)
                && !string.Equals(value.Trim(), currentUserId, StringComparison.Ordinal)
            )
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"{propertyName} must match the current user.");
            }
        }
    }
}
