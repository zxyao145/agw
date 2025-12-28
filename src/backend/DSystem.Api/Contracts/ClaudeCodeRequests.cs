namespace DSystem.Api.Contracts;

/// <summary>
/// Request for executing ClaudeCode query.
/// </summary>
public record ClaudeCodeExecuteRequest
{
    /// <summary>
    /// User prompt to send to ClaudeCode.
    /// </summary>
    public required string Input { get; init; }

    /// <summary>
    /// Working directory for ClaudeCode (optional).
    /// If not specified, uses the current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Anthropic API key (optional).
    /// If not specified, uses ANTHROPIC_AUTH_TOKEN environment variable.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Anthropic base URL (optional).
    /// If not specified, uses default API URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// System prompt for ClaudeCode (optional).
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Maximum number of turns (optional).
    /// </summary>
    public int? MaxTurns { get; init; }
}
