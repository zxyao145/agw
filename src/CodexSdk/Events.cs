using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSdk;

public sealed record Usage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("cached_input_tokens")]
    public int CachedInputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

public sealed record ThreadError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public abstract record ThreadEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed record ThreadStartedEvent : ThreadEvent
{
    [JsonPropertyName("thread_id")]
    public required string ThreadId { get; init; }
}

public sealed record TurnStartedEvent : ThreadEvent;

public sealed record TurnCompletedEvent : ThreadEvent
{
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }
}

public sealed record TurnFailedEvent : ThreadEvent
{
    [JsonPropertyName("error")]
    public required ThreadError Error { get; init; }
}

public sealed record ItemStartedEvent : ThreadEvent
{
    [JsonIgnore]
    public required ThreadItem Item { get; init; }
}

public sealed record ItemUpdatedEvent : ThreadEvent
{
    [JsonIgnore]
    public required ThreadItem Item { get; init; }
}

public sealed record ItemCompletedEvent : ThreadEvent
{
    [JsonIgnore]
    public required ThreadItem Item { get; init; }
}

public sealed record ThreadErrorEvent : ThreadEvent
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

internal static class ThreadEventParser
{
    public static ThreadEvent Parse(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        return type switch
        {
            "thread.started" => root.Deserialize<ThreadStartedEvent>(JsonDefaults.Options)!,
            "turn.started" => root.Deserialize<TurnStartedEvent>(JsonDefaults.Options)!,
            "turn.completed" => root.Deserialize<TurnCompletedEvent>(JsonDefaults.Options)!,
            "turn.failed" => root.Deserialize<TurnFailedEvent>(JsonDefaults.Options)!,
            "item.started" => new ItemStartedEvent { Type = type!, Item = ThreadItemParser.Parse(root.GetProperty("item")) },
            "item.updated" => new ItemUpdatedEvent { Type = type!, Item = ThreadItemParser.Parse(root.GetProperty("item")) },
            "item.completed" => new ItemCompletedEvent { Type = type!, Item = ThreadItemParser.Parse(root.GetProperty("item")) },
            "error" => root.Deserialize<ThreadErrorEvent>(JsonDefaults.Options)!,
            _ => throw new InvalidOperationException($"Unsupported thread event type '{type}'."),
        };
    }
}
