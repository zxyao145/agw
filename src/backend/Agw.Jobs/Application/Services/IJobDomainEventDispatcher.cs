using Agw.Jobs.Domain.Events;

namespace Agw.Jobs.Application.Services;

public interface IJobDomainEventDispatcher
{
    event Func<IJobDomainEvent, CancellationToken, Task>? DomainEventDispatched;

    Task DispatchAsync(IJobDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
