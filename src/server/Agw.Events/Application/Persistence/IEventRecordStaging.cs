using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Application.Persistence;

public interface IEventRecordStaging
{
    Task<EventPublishResult> StageSourceAsync(
        EventSource source,
        CloudEventDraft draft,
        EventStagingRequest? lineage,
        CancellationToken cancellationToken
    );
}
