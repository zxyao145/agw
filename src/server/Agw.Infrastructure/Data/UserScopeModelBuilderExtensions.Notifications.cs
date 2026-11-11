using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

internal static partial class UserScopeModelBuilderExtensions
{
    private static void ApplyNotificationScopeFilters(ModelBuilder builder, AgwDbContext context)
    {
        builder
            .Entity<BackgroundExecution>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<Notification>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<NotificationQuestion>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.Notifications.Any(notification =>
                            notification.Id == row.NotificationId && notification.CreateBy == context.CurrentUserId
                        )
            );
        builder
            .Entity<NotificationUpdateReceipt>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.Notifications.Any(notification =>
                            notification.Id == row.NotificationId && notification.CreateBy == context.CurrentUserId
                        )
            );
        builder
            .Entity<NotificationDispatch>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.Notifications.Any(notification =>
                            notification.Id == row.NotificationId && notification.CreateBy == context.CurrentUserId
                        )
            );
        builder
            .Entity<NotificationDispatchAttempt>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.NotificationDispatches.Any(dispatch =>
                            dispatch.Id == row.DispatchId
                            && context.Notifications.Any(notification =>
                                notification.Id == dispatch.NotificationId
                                && notification.CreateBy == context.CurrentUserId
                            )
                        )
            );
    }
}
