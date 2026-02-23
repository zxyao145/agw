using System.Text.Json.Serialization;

namespace DSystem.Shared.Contracts;

/// <summary>
/// OpenAI-compatible chat completion request
/// </summary>
public record OpenAIChatCompletionRequest
{
    /// <summary>
    /// Model ID (maps to Agent ID in our system)
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// List of messages comprising the conversation
    /// </summary>
    [JsonPropertyName("messages")]
    public required List<OpenAIChatMessage> Messages { get; init; }

    /// <summary>
    /// Whether to stream the response
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = false;

    /// <summary>
    /// Temperature for sampling (0-2)
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>
    /// Maximum tokens to generate
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Thread ID for conversation continuation (custom extension)
    /// </summary>
    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }
}

/// <summary>
/// OpenAI chat message
/// </summary>
public record OpenAIChatMessage
{
    /// <summary>
    /// Role: system, user, assistant, or function
    /// </summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>
    /// Message content
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Name of the author (optional)
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// OpenAI-compatible chat completion response
/// </summary>
public record OpenAIChatCompletionResponse
{
    /// <summary>
    /// Unique identifier for the completion
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Object type (always "chat.completion")
    /// </summary>
    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion";

    /// <summary>
    /// Unix timestamp of creation
    /// </summary>
    [JsonPropertyName("created")]
    public long Created { get; init; }

    /// <summary>
    /// Model used (Agent ID in our system)
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// List of completion choices
    /// </summary>
    [JsonPropertyName("choices")]
    public required List<OpenAIChatChoice> Choices { get; init; }

    /// <summary>
    /// Token usage statistics
    /// </summary>
    [JsonPropertyName("usage")]
    public OpenAIUsage? Usage { get; init; }

    /// <summary>
    /// Thread ID for conversation continuation (custom extension)
    /// </summary>
    [JsonPropertyName("thread_id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// OpenAI chat completion choice
/// </summary>
public record OpenAIChatChoice
{
    /// <summary>
    /// Choice index
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// The message returned
    /// </summary>
    [JsonPropertyName("message")]
    public OpenAIChatMessage? Message { get; init; }

    /// <summary>
    /// Reason for finishing (stop, length, function_call, etc.)
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

/// <summary>
/// OpenAI-compatible streaming chunk response
/// </summary>
public record OpenAIChatCompletionChunk
{
    /// <summary>
    /// Unique identifier for the completion
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Object type (always "chat.completion.chunk")
    /// </summary>
    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion.chunk";

    /// <summary>
    /// Unix timestamp of creation
    /// </summary>
    [JsonPropertyName("created")]
    public long Created { get; init; }

    /// <summary>
    /// Model used (Agent ID in our system)
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// List of completion choices
    /// </summary>
    [JsonPropertyName("choices")]
    public required List<OpenAIChatChunkChoice> Choices { get; init; }

    /// <summary>
    /// Thread ID for conversation continuation (custom extension)
    /// </summary>
    [JsonPropertyName("thread_id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// OpenAI streaming chat choice
/// </summary>
public record OpenAIChatChunkChoice
{
    /// <summary>
    /// Choice index
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// Delta content for this chunk
    /// </summary>
    [JsonPropertyName("delta")]
    public OpenAIChatDelta? Delta { get; init; }

    /// <summary>
    /// Reason for finishing (null until the last chunk)
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

/// <summary>
/// Delta content in streaming response
/// </summary>
public record OpenAIChatDelta
{
    /// <summary>
    /// Role (only in first chunk)
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>
    /// Content chunk
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

/// <summary>
/// Token usage statistics
/// </summary>
public record OpenAIUsage
{
    /// <summary>
    /// Number of tokens in the prompt
    /// </summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    /// <summary>
    /// Number of tokens in the completion
    /// </summary>
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }

    /// <summary>
    /// Total tokens used
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}
