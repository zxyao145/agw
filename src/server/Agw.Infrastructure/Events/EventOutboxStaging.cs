using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventOutboxStaging : IOutboxStaging
{
    private readonly AgwDbContext _db;
    private readonly EventsOptions _options;
    private readonly PendingTransportMessages _pending;
    private readonly TimeProvider _timeProvider;

    public EventOutboxStaging(
        AgwDbContext db,
        IOptions<EventsOptions> options,
        PendingTransportMessages pending,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _options = options.Value;
        _pending = pending;
        _timeProvider = timeProvider;
    }

    public void Stage(OutboxMessage message)
    {
        if (!_options.Outbox.Enabled)
        {
            _pending.Stage(message);
            return;
        }
        _db.EventOutbox.Add(
            new EventOutbox
            {
                Id = Guid.CreateVersion7(),
                Topic = message.Topic,
                EntityId = message.EntityId,
                State = EventOutboxState.Pending,
            }
        );
    }

    public async Task CompleteAsync(string topic, Guid entityId, CancellationToken cancellationToken)
    {
        if (!_options.Outbox.Enabled)
            return;
        var records = await _db
            .EventOutbox.Where(row =>
                row.Topic == topic && row.EntityId == entityId && row.State != EventOutboxState.Completed
            )
            .ToListAsync(cancellationToken);
        foreach (var record in records)
        {
            record.State = EventOutboxState.Completed;
            record.CompletedAt = _timeProvider.GetUtcNow();
        }
    }
}
