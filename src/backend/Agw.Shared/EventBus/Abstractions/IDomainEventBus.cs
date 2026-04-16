namespace Agw.Shared.EventBus.Abstractions;

public interface IDomainEventBus
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
