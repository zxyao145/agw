using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Domain.ValueObjects;

public sealed record EventScheduleDefinition
{
    public required EventScheduleKind Kind { get; init; }

    public required DateTimeOffset DueAt { get; init; }

    public TimeSpan? Interval { get; init; }

    public string? Cron { get; init; }

    public required string TimeZone { get; init; }

    public required string EventType { get; init; }

    public string? Subject { get; init; }

    public string? DataContentType { get; init; }

    public string? DataSchema { get; init; }

    public string? Summary { get; init; }

    public string? Link { get; init; }

    public string? Data { get; init; }

    public bool DataIsBinary { get; init; }

    public string? Scope { get; init; }

    public string? TagsJson { get; init; }

    public Guid ChainId { get; init; }

    public Guid? ParentEventId { get; init; }

    public int Hops { get; init; }
}
