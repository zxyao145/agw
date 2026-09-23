using System.Globalization;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Settings;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Infrastructure.Data.Interceptors;

/// <summary>
/// <para>写入所处的审计阶段；所有权规则在新建与修改两个阶段的判定不同。</para>
/// <para>Audit stage of the write; the ownership rule decides differently for the created and modified stages.</para>
/// </summary>
internal enum EntityOwnerStage
{
    Created,
    Modified,
}

/// <summary>
/// <para>校验 CreateBy 与 UserId 属于当前用户，两个审计阶段共用同一份规则。</para>
/// <para>Validates that CreateBy and UserId belong to the current user, shared by both audit stages.</para>
/// </summary>
internal static class EntityOwnerGuard
{
    public static void EnsureOwnerMatchesCurrentUser(EntityEntry entry, EntityOwnerStage stage)
    {
        if (entry.Entity is Setting { UserId: null })
            return;
        if (!UserInfoUtil.IsContextActive || UserInfoUtil.IsSystemScopeActive)
        {
            return;
        }

        // JobLog 由调度上下文代写，新建时不要求所有者等于当前用户。
        // JobLog rows are written on behalf of the scheduler, so creation does not require the owner to be the current user.
        if (stage == EntityOwnerStage.Created && entry.Entity is JobLog)
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

            var rawValue = entry.Property(propertyName).CurrentValue;
            var value = rawValue is long numericId
                ? numericId.ToString(CultureInfo.InvariantCulture)
                : rawValue as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (propertyName != "UserId")
                {
                    continue;
                }

                // 新建时补齐缺失的 UserId；修改时缺失即为非法。
                // Creation fills in a missing UserId; modification treats it as invalid.
                if (stage == EntityOwnerStage.Modified)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "UserId is required.");
                }

                entry.Property(propertyName).CurrentValue = currentUserId;
                continue;
            }

            if (!string.Equals(value.Trim(), currentUserId, StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"{propertyName} must match the current user.");
            }
        }
    }
}
