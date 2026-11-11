using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Application.Persistence;

public interface IEventDispatchNoticeStaging
{
    Task StageHopLimitAsync(EventRecord record, CancellationToken cancellationToken);
}

public interface IEventExecutionCompletionHandler
{
    Task HandleAsync(EventRecord record, EventSource source, CancellationToken cancellationToken);
}
