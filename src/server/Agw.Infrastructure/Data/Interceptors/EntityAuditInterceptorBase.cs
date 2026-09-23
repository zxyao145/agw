using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Data.Interceptors;

/// <summary>
/// <para>审计拦截器的公共骨架：保存前取得审计用户与时间，再把每个跟踪条目交给派生类。</para>
/// <para>Shared skeleton for audit interceptors: resolves the audit user and time before save, then hands each tracked entry to the derived class.</para>
/// </summary>
public abstract class EntityAuditInterceptorBase : SaveChangesInterceptor
{
    private readonly IEntityAuditUserIdProvider _entityAuditUserIdProvider;
    private readonly TimeProvider _timeProvider;

    protected EntityAuditInterceptorBase(
        IEntityAuditUserIdProvider entityAuditUserIdProvider,
        TimeProvider timeProvider
    )
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

    /// <summary>
    /// <para>处理单个跟踪条目；派生类自行判断实体状态，并决定校验与标记的先后顺序。</para>
    /// <para>Handles one tracked entry; the derived class checks the entity state and decides the order of validation and stamping.</para>
    /// </summary>
    protected abstract void OnSavingEntry(EntityEntry entry, string userId, DateTimeOffset now);

    private void BeforeSaveChanges(DbContextEventData eventData, string userId, DateTimeOffset now)
    {
        if (eventData.Context is null)
        {
            return;
        }
        foreach (var entry in eventData.Context.ChangeTracker.Entries())
        {
            OnSavingEntry(entry, userId, now);
        }
    }
}
