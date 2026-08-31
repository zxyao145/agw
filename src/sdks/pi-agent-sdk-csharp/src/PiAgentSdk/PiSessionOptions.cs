using System.Text.Json.Serialization;

namespace PiAgentSdk;

/// <summary>Specifies whether Pi may trust project-local configuration.</summary>
public enum PiProjectTrust
{
    /// <summary>Denies project trust and starts Pi with <c>--no-approve</c>.</summary>
    Deny,

    /// <summary>Approves project trust and starts Pi with <c>--approve</c>.</summary>
    Approve,
}

/// <summary>Configures one Pi RPC process and its provider-side conversation.</summary>
public sealed record PiSessionOptions
{
    private static readonly HashSet<string> ThinkingLevels =
    [
        "off",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
    ];

    /// <summary>Gets the working directory exposed to Pi and its tools.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the provider identifier selected for the session.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the provider model identifier selected for the session.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the Pi thinking level, such as <c>low</c>, <c>high</c>, or <c>off</c>.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>Gets the persistent-session directory passed through <c>--session-dir</c>.</summary>
    /// <remarks>This CLI option takes precedence over <c>PI_CODING_AGENT_SESSION_DIR</c>.</remarks>
    public string? SessionDir { get; init; }

    /// <summary>Gets the optional display name assigned to a newly created Pi session.</summary>
    public string? SessionName { get; init; }

    /// <summary>Gets a value indicating whether Pi should avoid persistent session storage.</summary>
    /// <remarks>Ephemeral sessions cannot be resumed.</remarks>
    public bool NoSession { get; init; }

    /// <summary>Gets the project trust policy. The default denies trust.</summary>
    public PiProjectTrust ProjectTrust { get; init; } = PiProjectTrust.Deny;

    /// <summary>Gets an optional allowlist of Pi tool names.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Gets an optional list of Pi tool names to disable.</summary>
    public IReadOnlyList<string>? ExcludeTools { get; init; }

    /// <summary>Gets a value indicating whether Pi extensions are disabled.</summary>
    public bool NoExtensions { get; init; }

    /// <summary>Gets explicit extension file paths passed through repeated <c>--extension</c> options.</summary>
    /// <remarks>
    /// Explicit paths remain loadable when <see cref="NoExtensions"/> disables automatic extension discovery.
    /// </remarks>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>Gets per-session environment variables applied after global SDK overrides.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>Gets the asynchronous handler for blocking Extension UI dialog requests.</summary>
    /// <remarks>The handler is runtime-only and is not serialized with session options.</remarks>
    [JsonIgnore]
    public Func<
        PiExtensionUiRequest,
        CancellationToken,
        ValueTask<PiExtensionUiResponse>
    >? ExtensionUiHandler { get; init; }

    internal void Validate(bool isResume)
    {
        if (NoSession && isResume)
        {
            throw new ArgumentException("An ephemeral Pi session cannot be resumed.", nameof(NoSession));
        }

        if (!string.IsNullOrWhiteSpace(ThinkingLevel) && !ThinkingLevels.Contains(ThinkingLevel))
        {
            throw new ArgumentException($"Unsupported Pi thinking level '{ThinkingLevel}'.", nameof(ThinkingLevel));
        }

        ValidateToolNames(Tools, nameof(Tools));
        ValidateToolNames(ExcludeTools, nameof(ExcludeTools));
        if (Extensions?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException("Pi extension paths cannot be empty.", nameof(Extensions));
        }
    }

    private static void ValidateToolNames(IReadOnlyList<string>? names, string parameterName)
    {
        if (names?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException("Pi tool names cannot be empty.", parameterName);
        }
    }
}
