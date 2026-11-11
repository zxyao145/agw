using System.Data.Common;
using Agw.Events.Contracts.Transport;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Events;

public sealed class EventCommitCoordinator
{
    private readonly PendingTransportMessages _pending;
    private readonly IServiceProvider _services;
    private readonly ILogger<EventCommitCoordinator> _logger;

    public EventCommitCoordinator(
        PendingTransportMessages pending,
        IServiceProvider services,
        ILogger<EventCommitCoordinator> logger
    )
    {
        _pending = pending;
        _services = services;
        _logger = logger;
    }

    public async Task CommittedAsync()
    {
        try
        {
            if (_pending.HasMessages)
                await _pending.FlushAsync(_services.GetRequiredService<IEventTransport>(), _logger);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Event transport could not be resolved after commit.");
        }
        finally
        {
            await _pending.DisposeAsync();
        }
    }

    public ValueTask RolledBackAsync() => _pending.DisposeAsync();

    public void ValidateLeases() => _pending.ValidateLeases();
}

public sealed class EventSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly EventCommitCoordinator _coordinator;

    public EventSaveChangesInterceptor(EventCommitCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        _coordinator.ValidateLeases();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        _coordinator.ValidateLeases();
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.Context?.Database.CurrentTransaction is null)
            await _coordinator.CommittedAsync();
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context?.Database.CurrentTransaction is null)
            _coordinator.CommittedAsync().GetAwaiter().GetResult();
        return result;
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => _coordinator.RolledBackAsync().AsTask();

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        _coordinator.RolledBackAsync().AsTask().GetAwaiter().GetResult();
}

public sealed class EventTransactionInterceptor : DbTransactionInterceptor
{
    private readonly EventCommitCoordinator _coordinator;

    public EventTransactionInterceptor(EventCommitCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => _coordinator.CommittedAsync();

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        _coordinator.CommittedAsync().GetAwaiter().GetResult();

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => _coordinator.RolledBackAsync().AsTask();

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        _coordinator.RolledBackAsync().AsTask().GetAwaiter().GetResult();
}
