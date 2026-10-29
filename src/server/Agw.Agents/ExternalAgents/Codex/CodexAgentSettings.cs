using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Agents.ExternalAgents.Codex;

public sealed record CodexAgentSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public CodexClientSettings CodexOptions { get; init; } = new();

    public CodexThreadSettings ThreadOptions { get; init; } =
        new() { SandboxMode = CodexSandboxMode.DangerFullAccess, SkipGitRepoCheck = true };

    public CodexTurnSettings? TurnOptions { get; init; }

    public Guid? ThreadId { get; init; }

    public bool IsResume { get; init; }

    public JsonElement? OnThreadStartedAsync { get; init; }

    public JsonElement? ChatHistoryProvider { get; init; }

    public static CodexAgentSettings? Deserialize(string value) =>
        JsonSerializer.Deserialize<CodexAgentSettings>(value, SerializerOptions);
}

public sealed record CodexClientSettings
{
    public string? CodexPathOverride { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public JsonElement? Config { get; init; }

    public Dictionary<string, string>? Env { get; init; }
}

public sealed record CodexThreadSettings
{
    public string? Model { get; init; }

    public CodexSandboxMode? SandboxMode { get; init; }

    public string? WorkingDirectory { get; init; }

    public bool? SkipGitRepoCheck { get; init; }

    public CodexModelReasoningEffort? ModelReasoningEffort { get; init; }

    public long? ModelContextWindow { get; init; }

    public long? ModelAutoCompactTokenLimit { get; init; }

    public bool? NetworkAccessEnabled { get; init; }

    public CodexWebSearchMode? WebSearchMode { get; init; }

    public bool? WebSearchEnabled { get; init; }

    public CodexApprovalMode? ApprovalPolicy { get; init; }

    public IReadOnlyList<string>? AdditionalDirectories { get; init; }
}

public sealed record CodexTurnSettings
{
    public JsonElement? OutputSchema { get; init; }
}

public enum CodexSandboxMode
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess,
}

public enum CodexApprovalMode
{
    Never,
    OnRequest,
    OnFailure,
    Untrusted,
}

public enum CodexModelReasoningEffort
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

public enum CodexWebSearchMode
{
    Disabled,
    Cached,
    Live,
}
