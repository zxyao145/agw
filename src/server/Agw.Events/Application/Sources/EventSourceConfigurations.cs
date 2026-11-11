using System.Text.Json;

namespace Agw.Events.Application.Persistence;

public sealed record EventWebhookSourceConfig
{
    public required string Format { get; init; }

    public required EventWebhookAuthConfig Auth { get; init; }

    public IReadOnlyList<string> ContentTypes { get; init; } = ["application/json"];

    public EventWebhookMappingConfig? Mapping { get; init; }

    public EventWebhookDisplayMappingConfig? CloudEvent { get; init; }
}

public sealed record EventWebhookAuthConfig
{
    public required string Kind { get; init; }

    public string? SignatureHeader { get; init; }

    public string? TokenHeader { get; init; }

    public string? TimestampHeader { get; init; }

    public int? MaxClockSkewSeconds { get; init; }
}

public sealed record EventWebhookDisplayMappingConfig
{
    public JsonElement? Summary { get; init; }

    public JsonElement? Link { get; init; }
}

public sealed record EventHttpPollSourceConfig
{
    public required EventHttpPollRequestConfig Request { get; init; }

    public EventHttpPollPaginationConfig Pagination { get; init; } = new();

    public required string Items { get; init; }

    public required JsonElement Key { get; init; }

    public required EventHttpPollChangeConfig Change { get; init; }

    public required EventHttpPollEventConfig Event { get; init; }

    public required string Interval { get; init; }
}

public sealed record EventHttpPollRequestConfig
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public JsonElement? Body { get; init; }

    public EventHttpPollAuthConfig Auth { get; init; } = new();
}

public sealed record EventHttpPollAuthConfig
{
    public string Kind { get; init; } = "none";

    public Guid? ConnectionId { get; init; }

    public string? HeaderName { get; init; }
}

public sealed record EventHttpPollPaginationConfig
{
    public string Kind { get; init; } = "none";

    public int? MaxPages { get; init; }

    public string? CursorPath { get; init; }

    public string? CursorParameter { get; init; }

    public string? PageParameter { get; init; }
}

public sealed record EventHttpPollChangeConfig
{
    public required string Kind { get; init; }

    public JsonElement? Value { get; init; }
}

public sealed record EventHttpPollEventConfig
{
    public required string TypeOnNew { get; init; }

    public required string TypeOnChange { get; init; }

    public JsonElement? Subject { get; init; }

    public JsonElement? Time { get; init; }

    public string? DataSchema { get; init; }

    public string? Scope { get; init; }

    public JsonElement? Tags { get; init; }

    public JsonElement? Summary { get; init; }

    public JsonElement? Link { get; init; }
}

public sealed record EventTimerSourceConfig
{
    public required string Kind { get; init; }

    public DateTimeOffset? At { get; init; }

    public string? Interval { get; init; }

    public string? Cron { get; init; }

    public required string TimeZone { get; init; }

    public required EventSourceEventTemplate Event { get; init; }
}

public sealed record EventSourceEventTemplate
{
    public required string Type { get; init; }

    public DateTimeOffset? Time { get; init; }

    public string? Subject { get; init; }

    public string? DataContentType { get; init; }

    public JsonElement? Data { get; init; }

    public string? DataBase64 { get; init; }

    public string? DataSchema { get; init; }

    public string? Scope { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string? Summary { get; init; }

    public string? Link { get; init; }
}
