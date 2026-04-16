using Microsoft.Extensions.DependencyInjection;

namespace Agw.Shared.EventBus.Abstractions;

public sealed class InMemoryDomainEventBus(IServiceScopeFactory scopeFactory) : IDomainEventBus
{
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        using var scope = scopeFactory.CreateScope();
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
        var handlers = scope.ServiceProvider.GetServices(handlerType);

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handleTask = (Task?)handlerType
                .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!
                .Invoke(handler, [domainEvent, cancellationToken]);

            if (handleTask != null)
            {
                await handleTask;
            }
        }
    }
}
