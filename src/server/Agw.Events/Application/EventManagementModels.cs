using System.Text.Json;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Application;

public sealed record EventSubscriptionRequest
{
    public Guid? Id { get; init; }
    public long? Version { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<Guid> SourceIds { get; init; } = [];
    public IReadOnlyList<string> PublisherKeys { get; init; } = [];
    public required IReadOnlyList<string> EventTypes { get; init; }
    public JsonElement? Filter { get; init; }
    public required EventSubscriptionTargetKind TargetKind { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? Instructions { get; init; }
    public int? PermissionMode { get; init; }
    public Guid? HandlerExtensionId { get; init; }
    public string? HandlerId { get; init; }
    public JsonElement? HandlerConfig { get; init; }
}

public sealed record EventSubscriptionDeleteCommand(Guid Id, long Version);

public sealed record EventListFilter
{
    public Guid? SourceId { get; init; }
    public string? Type { get; init; }
    public string? Subject { get; init; }
    public Guid? ChainId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record EventSubscriptionResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
    public required IReadOnlyList<string> PublisherKeys { get; init; }
    public required IReadOnlyList<string> EventTypes { get; init; }
    public JsonElement? Filter { get; init; }
    public required EventSubscriptionTargetKind TargetKind { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? Instructions { get; init; }
    public int? PermissionMode { get; init; }
    public Guid? HandlerExtensionId { get; init; }
    public string? HandlerId { get; init; }
    public JsonElement? HandlerConfig { get; init; }
    public required string ManagedBy { get; init; }
    public string? ManagedKey { get; init; }
    public long? ManagedVersion { get; init; }
    public EventSubscriptionManagedState? ManagedState { get; init; }
    public required long Version { get; init; }
}

public sealed record EventView
{
    public required Guid Id { get; init; }
    public required Guid SourceId { get; init; }
    public required EventCloudEvent CloudEvent { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required Guid ChainId { get; init; }
    public required int Hops { get; init; }
    public required EventDispatchState DispatchState { get; init; }
    public required int MatchedCount { get; init; }
}

public sealed record EventCloudEvent
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset? Time { get; init; }
    public string? Subject { get; init; }
    public string? DataContentType { get; init; }
    public string? DataSchema { get; init; }
    public CloudEventData? Data { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Summary { get; init; }
    public string? Link { get; init; }
    public Guid? ParentEventId { get; init; }
}

public sealed record EventDeliveryResponse
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required EventDeliveryState State { get; init; }
    public required int Attempt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record EventScheduleResponse
{
    public required Guid Id { get; init; }
    public EventScheduleKind? Kind { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public TimeSpan? Interval { get; init; }
    public string? Cron { get; init; }
    public string? TimeZone { get; init; }
    public string? EventType { get; init; }
    public string? Subject { get; init; }
    public string? Summary { get; init; }
    public required EventScheduleState State { get; init; }
    public required long Occurrence { get; init; }
    public DateTimeOffset? LastFiredAt { get; init; }
}
