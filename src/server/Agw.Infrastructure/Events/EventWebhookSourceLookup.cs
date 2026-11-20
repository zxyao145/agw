using Agw.Events.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Events;

public sealed class EventWebhookSourceLookup : IEventWebhookSourceLookup
{
    private readonly AgwDbContext _db;

    public EventWebhookSourceLookup(AgwDbContext db)
    {
        _db = db;
    }

    public Task<EventSource?> FindEnabledAsync(Guid sourceId, CancellationToken cancellationToken) =>
        _db
            .EventSources.IgnoreUserScope()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == sourceId && row.Kind == EventSourceKind.Webhook && row.Enabled && row.DeletedAt == null,
                cancellationToken
            );
}
