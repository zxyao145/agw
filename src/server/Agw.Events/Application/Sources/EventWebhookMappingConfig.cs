using System.Text.Json;

namespace Agw.Events.Application.Persistence;

public sealed record EventWebhookMappingConfig
{
    public required JsonElement Id { get; init; }

    public required JsonElement Type { get; init; }

    public JsonElement? Subject { get; init; }

    public JsonElement? Time { get; init; }

    public JsonElement? DataSchema { get; init; }

    public JsonElement? Scope { get; init; }

    public JsonElement? Tags { get; init; }

    public JsonElement? Summary { get; init; }

    public JsonElement? Link { get; init; }
}

public sealed record WebhookRequestContext
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public JsonElement? BodyJson { get; init; }

    public string? BodyText { get; init; }

    public byte[]? BodyBinary { get; init; }

    public string? DataContentType { get; init; }
}
