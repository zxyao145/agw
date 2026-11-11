using System.Text.Json;

namespace Agw.Events.Contracts;

public interface IEventSubscriptionFacade
{
    Task<ManagedSubscriptionState> UpsertManagedAsync(
        ManagedSubscriptionRequest request,
        CancellationToken cancellationToken
    );

    Task<ManagedSubscriptionState> DeleteManagedAsync(
        string managedKey,
        long managedVersion,
        CancellationToken cancellationToken
    );
}

public enum ManagedSubscriptionStatus
{
    Active,
    Deleted,
}

public sealed record ManagedSubscriptionRequest
{
    public required string ManagedKey { get; init; }

    public required long ManagedVersion { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<Guid> SourceIds { get; init; } = [];

    public IReadOnlyList<string> PublisherKeys { get; init; } = [];

    public required IReadOnlyList<string> EventTypes { get; init; }

    public JsonElement? Filter { get; init; }

    public required string HandlerId { get; init; }

    public required JsonElement HandlerConfig { get; init; }
}

public sealed record ManagedSubscriptionState
{
    public required Guid SubscriptionId { get; init; }

    public required long ManagedVersion { get; init; }

    public required ManagedSubscriptionStatus Status { get; init; }

    public required bool Accepted { get; init; }
}
