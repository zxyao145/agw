using ClaudeCodeSdk.Types;

namespace DSystem.ExternalAgents;

/// <summary>
/// Message type for WebSocket communication.
/// </summary>
public enum ClaudeCodeMessageType
{
    /// <summary>
    /// Initialize session with configuration.
    /// </summary>
    Setting = 0,

    /// <summary>
    /// Execute input with existing session.
    /// </summary>
    Input = 1
}

/// <summary>
/// Wrapper for WebSocket requests.
/// </summary>
public record ClaudeCodeWsRequest
{
    /// <summary>
    /// Message type.
    /// </summary>
    public required ClaudeCodeMessageType Type { get; init; }

    /// <summary>
    /// Init request (present when Type is Init).
    /// </summary>
    public ClaudeCodeSettingRequest? Setting { get; init; }

    /// <summary>
    /// Input request (present when Type is Input).
    /// </summary>
    public ClaudeCodeInputRequest? Input { get; init; }
}

/// <summary>
/// Request for initializing ClaudeCode session (sent once on WebSocket connection).
/// </summary>
public record ClaudeCodeSettingRequest
{
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
    public string? ApiBaseUrl { get; init; }

    /// <summary>
    /// System prompt for ClaudeCode (optional).
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Maximum number of turns (optional).
    /// </summary>
    public int? MaxTurns { get; init; }

    /// <summary>
    /// Thread ID for conversation context.
    /// </summary>
    public string SessionId { get; init; } = "";

    /// <summary>
    /// Permission mode for tool execution.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Environment variables (optional).
    /// </summary>
    public Dictionary<string, string?>? EnvironmentVariables { get; init; }
}

/// <summary>
/// Request for executing input with existing session (sent after initialization).
/// </summary>
public record ClaudeCodeInputRequest
{
    /// <summary>
    /// User prompt to send to ClaudeCode.
    /// </summary>
    public required string Input { get; init; }
}
