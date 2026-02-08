using System.Text.Json.Serialization;

namespace DSystem.Shared.Contracts;

/// <summary>
/// Stream options for OpenAI Responses API
/// </summary>
public class ThorStreamOptions
{
    /// <summary>
    /// If set, an additional chunk will be streamed before the data: [DONE] message.
    /// The usage field on this chunk shows the token usage statistics for the entire request,
    /// and the choices field will always be an empty array.
    /// </summary>
    [JsonPropertyName("include_usage")]
    public bool? IncludeUsage { get; set; }
}

/// <summary>
/// Reasoning configuration for OpenAI Responses API
/// </summary>
public class ReasoningResponsesInput
{
    /// <summary>
    /// Effort level for reasoning (low, medium, high)
    /// </summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    /// <summary>
    /// Maximum reasoning tokens
    /// </summary>
    [JsonPropertyName("max_reasoning_tokens")]
    public int? MaxReasoningTokens { get; set; }
}

/// <summary>
/// Tool configuration for OpenAI Responses API
/// </summary>
public class ResponsesToolsInput
{
    /// <summary>
    /// Tool type (e.g., "function", "code_interpreter", "file_search")
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>
    /// Function definition (when type is "function")
    /// </summary>
    [JsonPropertyName("function")]
    public ResponsesToolFunction? Function { get; set; }
}

/// <summary>
/// Function definition for tools
/// </summary>
public class ResponsesToolFunction
{
    /// <summary>
    /// Function name
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// Function description
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Function parameters schema (JSON Schema object)
    /// </summary>
    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }

    /// <summary>
    /// Whether to show the function output to the user
    /// </summary>
    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}
