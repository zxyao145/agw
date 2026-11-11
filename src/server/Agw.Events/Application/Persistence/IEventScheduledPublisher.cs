using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Application.Persistence;

public interface IEventScheduledPublisher
{
    Task StageAsync(EventSchedule schedule, CloudEventDraft draft, CancellationToken cancellationToken);
}
