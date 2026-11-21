namespace Agw.Shared.Contracts.Events;

public sealed record CloudEventDraft
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Source { get; init; }

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
