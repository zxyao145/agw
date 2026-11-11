namespace Agw.Shared.Contracts.Events;

public sealed record CloudEventEnvelope
{
    public required string Id { get; init; }

    public required string Source { get; init; }

    public required string Type { get; init; }

    public DateTimeOffset? Time { get; init; }

    public string? Subject { get; init; }

    public string? DataContentType { get; init; }

    public string? DataSchema { get; init; }

    public CloudEventData? Data { get; init; }

    public IReadOnlyDictionary<string, CloudEventAttributeValue> Extensions { get; init; } =
        new Dictionary<string, CloudEventAttributeValue>();
}
