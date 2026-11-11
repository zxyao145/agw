using Agw.Shared.Contracts.Events;

namespace Agw.Events.Contracts;

public interface IEventPublisher
{
    Task<EventPublishResult> PublishAsync(CloudEventDraft draft, CancellationToken cancellationToken);
}

public sealed record EventPublishResult(Guid EventId, bool Duplicate);

public interface IEventStaging
{
    Task<EventPublishResult> StageAsync(EventStagingRequest request, CancellationToken cancellationToken);
}

public sealed record EventStagingRequest
{
    public required string PublisherKey { get; init; }

    public required CloudEventDraft Event { get; init; }

    public bool InheritParentHops { get; init; }

    public Guid? ChainId { get; init; }

    public int? Hops { get; init; }
}

public interface IOutboxStaging
{
    void Stage(OutboxMessage message);

    Task CompleteAsync(string topic, Guid entityId, CancellationToken cancellationToken);
}

public sealed record OutboxMessage
{
    public required string Topic { get; init; }

    public required Guid EntityId { get; init; }

    public required CloudEventEnvelope Message { get; init; }
}

public interface IEventSourceFacade
{
    Task<EventSourceIdentity?> GetAsync(Guid sourceId, CancellationToken cancellationToken);
}

public interface IEventContextFacade
{
    Task<EventContext?> GetAsync(Guid eventId, CancellationToken cancellationToken);
}

public sealed record EventContext
{
    public required Guid EventId { get; init; }

    public required Guid SourceId { get; init; }

    public required Guid ChainId { get; init; }

    public Guid? ParentEventId { get; init; }

    public required int Hops { get; init; }
}

public sealed record EventSourceIdentity
{
    public required Guid SourceId { get; init; }

    public required EventSourceKind Kind { get; init; }

    public string? PublisherKey { get; init; }

    public required string Source { get; init; }
}
