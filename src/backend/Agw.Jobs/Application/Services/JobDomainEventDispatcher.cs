using Agw.Jobs.Domain.Events;

namespace Agw.Jobs.Application.Services;

public class JobDomainEventDispatcher : IJobDomainEventDispatcher
{
    public event Func<IJobDomainEvent, CancellationToken, Task>? DomainEventDispatched;

    public async Task DispatchAsync(IJobDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var handlers = DomainEventDispatched;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<IJobDomainEvent, CancellationToken, Task>>())
        {
            await handler(domainEvent, cancellationToken);
        }
    }
}
