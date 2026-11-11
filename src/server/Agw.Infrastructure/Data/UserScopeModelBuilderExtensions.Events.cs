using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

internal static partial class UserScopeModelBuilderExtensions
{
    private static void ApplyEventScopeFilters(ModelBuilder builder, AgwDbContext context)
    {
        builder
            .Entity<EventSource>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<EventRecord>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<EventSubscription>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<EventDelivery>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<EventSchedule>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row => context.UserScopeBypass || context.UserScopeIsActive && row.CreateBy == context.CurrentUserId
            );
        builder
            .Entity<EventSourceItem>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.EventSources.Any(source =>
                            source.Id == row.SourceId && source.CreateBy == context.CurrentUserId
                        )
            );
        builder
            .Entity<EventDeliveryAttempt>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                row =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.EventDeliveries.Any(delivery =>
                            delivery.Id == row.DeliveryId && delivery.CreateBy == context.CurrentUserId
                        )
            );
    }
}
