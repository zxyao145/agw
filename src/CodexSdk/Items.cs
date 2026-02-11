using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSdk;

public abstract record ThreadItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed record CommandExecutionItem : ThreadItem
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("aggregated_output")]
    public required string AggregatedOutput { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record FileUpdateChange
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

public sealed record FileChangeItem : ThreadItem
{
    [JsonPropertyName("changes")]
    public required IReadOnlyList<FileUpdateChange> Changes { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record McpToolCallResult
{
    [JsonPropertyName("content")]
    public JsonElement[]? Content { get; init; }

    [JsonPropertyName("structured_content")]
    public JsonElement StructuredContent { get; init; }
}

public sealed record McpToolCallError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record McpToolCallItem : ThreadItem
{
    [JsonPropertyName("server")]
    public required string Server { get; init; }

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }

    [JsonPropertyName("result")]
    public McpToolCallResult? Result { get; init; }

    [JsonPropertyName("error")]
    public McpToolCallError? Error { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record AgentMessageItem : ThreadItem
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record ReasoningItem : ThreadItem
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record WebSearchItem : ThreadItem
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }
}

public sealed record ErrorItem : ThreadItem
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record TodoItem
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }
}

public sealed record TodoListItem : ThreadItem
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<TodoItem> Items { get; init; }
}

internal static class ThreadItemParser
{
    public static ThreadItem Parse(JsonElement element)
    {
        var type = element.GetProperty("type").GetString();
        return type switch
        {
            "agent_message" => element.Deserialize<AgentMessageItem>(JsonDefaults.Options)!,
            "reasoning" => element.Deserialize<ReasoningItem>(JsonDefaults.Options)!,
            "command_execution" => element.Deserialize<CommandExecutionItem>(JsonDefaults.Options)!,
            "file_change" => element.Deserialize<FileChangeItem>(JsonDefaults.Options)!,
            "mcp_tool_call" => element.Deserialize<McpToolCallItem>(JsonDefaults.Options)!,
            "web_search" => element.Deserialize<WebSearchItem>(JsonDefaults.Options)!,
            "todo_list" => element.Deserialize<TodoListItem>(JsonDefaults.Options)!,
            "error" => element.Deserialize<ErrorItem>(JsonDefaults.Options)!,
            _ => throw new InvalidOperationException($"Unsupported thread item type '{type}'."),
        };
    }
}
