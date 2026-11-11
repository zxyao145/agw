using Agw.Events.Application.Persistence;
using Agw.Infrastructure.Data;

namespace Agw.Infrastructure.Events;

public sealed class EventIngestTransaction : IEventIngestTransaction
{
    private readonly AgwDbContext _db;

    public EventIngestTransaction(AgwDbContext db)
    {
        _db = db;
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _db.ExecuteAndSaveAsync(action, cancellationToken);
}
