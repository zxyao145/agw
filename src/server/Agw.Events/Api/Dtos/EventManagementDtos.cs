using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Api.Dtos;

public sealed record EventIdRequest(Guid Id);

public sealed record EventSubscriptionDeleteRequest(Guid Id, long Version);

public sealed record EventResponse
{
    public required Guid Id { get; init; }
    public required Guid SourceId { get; init; }
    public required CloudEventResponse CloudEvent { get; init; }
    public string? Summary { get; init; }
    public string? Link { get; init; }
    public Guid? ParentEventId { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required Guid ChainId { get; init; }
    public required int Hops { get; init; }
    public required EventDispatchState DispatchState { get; init; }
    public required int MatchedCount { get; init; }
}

public sealed record CloudEventResponse
{
    [JsonPropertyName("specversion")]
    public required string SpecVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Time { get; init; }

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonPropertyName("datacontenttype")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataContentType { get; init; }

    [JsonPropertyName("dataschema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataSchema { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("data_base64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataBase64 { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record EventListQuery
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
