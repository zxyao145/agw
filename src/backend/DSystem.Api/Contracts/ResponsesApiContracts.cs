using System.Text.Json.Serialization;

namespace DSystem.Api.Contracts;

/// <summary>
/// OpenAI Responses API response object
/// </summary>
public record ResponsesApiResponse
{
    /// <summary>
    /// Unique identifier for the response
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Object type (always "response")
    /// </summary>
    [JsonPropertyName("object")]
    public string Object { get; init; } = "response";

    /// <summary>
    /// Unix timestamp of creation
    /// </summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    /// <summary>
    /// Model used
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// Response status (completed, failed, etc.)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "completed";

    /// <summary>
    /// List of output items
    /// </summary>
    [JsonPropertyName("output")]
    public required List<ResponsesOutputItem> Output { get; init; }

    /// <summary>
    /// Usage statistics
    /// </summary>
    [JsonPropertyName("usage")]
    public ResponsesUsage? Usage { get; init; }

    /// <summary>
    /// Previous response ID for conversation continuation
    /// </summary>
    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }
}

/// <summary>
/// Output item in Responses API
/// </summary>
public record ResponsesOutputItem
{
    /// <summary>
    /// Item index
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// Item type (message, function_call, etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    /// <summary>
    /// Message content (when type is "message")
    /// </summary>
    [JsonPropertyName("message")]
    public ResponsesMessage? Message { get; init; }

    /// <summary>
    /// Function call (when type is "function_call")
    /// </summary>
    [JsonPropertyName("function_call")]
    public object? FunctionCall { get; init; }
}

/// <summary>
/// Message in Responses API output
/// </summary>
public record ResponsesMessage
{
    /// <summary>
    /// Role (always "assistant" for output)
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    /// <summary>
    /// Message content
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

/// <summary>
/// Usage statistics for Responses API
/// </summary>
public record ResponsesUsage
{
    /// <summary>
    /// Input tokens
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    /// <summary>
    /// Output tokens
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    /// <summary>
    /// Total tokens
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}

/// <summary>
/// Streaming event for Responses API
/// </summary>
public record ResponsesStreamEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string? ObjectType { get; set; } = nameof(ResponsesStreamEvent);

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; set; }

    [JsonPropertyName("output")]
    public List<OutputItem> Output { get; set; } = new();
}

public class OutputItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("content")]
    public List<Content> Content { get; set; } = new();

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

public class Content
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("annotations")]
    public List<string> Annotations { get; set; } = new();

    [JsonPropertyName("logprobs")]
    public List<string>? Logprobs { get; set; } = null;

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

