namespace Agw.Events.Domain.ValueObjects;

public sealed record ManagedEventSubscriptionDefinition
{
    public required string Name { get; init; }

    public required string SourceIdsJson { get; init; }

    public string? PublisherKeysJson { get; init; }

    public required string EventTypesJson { get; init; }

    public string? FilterJson { get; init; }

    public required string HandlerId { get; init; }

    public required string HandlerConfigJson { get; init; }
}
