using Agw.Events.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;

namespace Agw.Infrastructure.Events;

public sealed class EventIngestTransaction : IEventIngestTransaction
{
    private readonly AgwDbContext _db;
    private readonly PendingTransportMessages _pending;
    private readonly IApplicationLock _applicationLock;

    public EventIngestTransaction(AgwDbContext db, PendingTransportMessages pending, IApplicationLock applicationLock)
    {
        _db = db;
        _pending = pending;
        _applicationLock = applicationLock;
    }

    public Task AcquireLockAsync(string resource, CancellationToken cancellationToken) =>
        _pending.AcquireAsync(_applicationLock, resource, cancellationToken);

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _db.ExecuteAndSaveAsync(action, cancellationToken);
}
