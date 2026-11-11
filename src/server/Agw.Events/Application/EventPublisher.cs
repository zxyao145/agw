using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;

namespace Agw.Events.Application;

public sealed class EventPublisher : IEventPublisher
{
    private readonly EventCaller _caller;
    private readonly IEventStaging _staging;
    private readonly IEventsDbContext _db;

    public EventPublisher(EventCaller caller, IEventStaging staging, IEventsDbContext db)
    {
        _caller = caller;
        _staging = staging;
        _db = db;
    }

    public async Task<EventPublishResult> PublishAsync(CloudEventDraft draft, CancellationToken cancellationToken)
    {
        var extension = _caller.RequiredExtension();
        _caller.ValidateType(extension, draft.Type, draft.ParentEventId is null);
        var result = await _staging.StageAsync(
            new EventStagingRequest { PublisherKey = $"ext:{extension.GenerationId.ExtensionId}", Event = draft },
            cancellationToken
        );
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
