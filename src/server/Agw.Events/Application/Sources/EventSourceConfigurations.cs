using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Shared.Contracts.Events;

namespace Agw.Events.Application.Sources;

public sealed record EventWebhookSourceConfig
{
    public IReadOnlyList<string>? EventTypes { get; init; }

    public string? FixedType { get; init; }

    public string Format { get; init; } = EventSourceFormats.CloudEvents;

    public EventWebhookAuthConfig Auth { get; init; } = new();

    public IReadOnlyList<string> ContentTypes { get; init; } = [];

    public EventWebhookMappingConfig? Mapping { get; init; }

    public EventWebhookDisplayMappingConfig? CloudEvent { get; init; }
}

public sealed record EventWebhookAuthConfig
{
    public string Kind { get; init; } = "BearerToken";

    public string? SignatureHeader { get; init; }

    public string? TokenHeader { get; init; } = "Authorization";

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
    public IReadOnlyList<string>? EventTypes { get; init; }

    public string? FixedType { get; init; }

    public required EventHttpPollRequestConfig Request { get; init; }

    public EventHttpPollPaginationConfig Pagination { get; init; } = new();

    public string Format { get; init; } = EventSourceFormats.CloudEvents;

    public EventHttpPollMappingConfig? Mapping { get; init; }

    public required string Interval { get; init; }
}

public sealed record EventHttpPollMappingConfig
{
    public required string Items { get; init; }

    public required JsonElement Key { get; init; }

    public required EventHttpPollChangeConfig Change { get; init; }

    public required EventHttpPollEventConfig Event { get; init; }
}

public sealed record EventHttpPollRequestConfig
{
    public string Method { get; init; } = "GET";

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
    internal const string None = "none";
    internal const string LinkHeader = "linkHeader";
    internal const string Page = "page";
    internal const string Cursor = "cursor";

    public string Kind { get; init; } = None;

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
    public string? TypeOnNew { get; init; }

    public string? TypeOnChange { get; init; }

    public JsonElement? Subject { get; init; }

    public JsonElement? Time { get; init; }

    public string? DataSchema { get; init; }

    public string? Scope { get; init; }

    public JsonElement? Tags { get; init; }

    public JsonElement? Summary { get; init; }

    public JsonElement? Link { get; init; }
}

public sealed record EventProviderSourceConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigurationId { get; init; }
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
