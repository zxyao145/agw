using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Application.Persistence;

public interface IEventPublisherSources
{
    Task<EventSource> EnsureBuiltInAsync(string name, CancellationToken cancellationToken);

    Task<EventSource> EnsureExtensionAsync(Guid extensionId, CancellationToken cancellationToken);

    Task EnsureAllBuiltInAsync(CancellationToken cancellationToken);
}
