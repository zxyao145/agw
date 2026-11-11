using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Events;

public sealed class EventVersionInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Stamp(DbContext? context)
    {
        if (context is null)
            return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var entityNamespace = entry.Metadata.ClrType.Namespace;
            if (entityNamespace is not ("Agw.Shared.Data.Entities.Events" or "Agw.Shared.Data.Entities.Notifications"))
                continue;
            if (entry.Metadata.FindProperty("Version")?.ClrType != typeof(long))
                continue;
            var version = entry.Property("Version");
            if (entry.State == EntityState.Added && (long)version.CurrentValue! == 0)
                version.CurrentValue = 1L;
            if (entry.State == EntityState.Modified && !version.IsModified)
                version.CurrentValue = checked((long)version.OriginalValue! + 1);
        }
    }
}
