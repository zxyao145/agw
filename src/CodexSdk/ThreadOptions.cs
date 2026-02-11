using System.Runtime.Serialization;

namespace CodexSdk;

public enum ApprovalMode
{
    [EnumMember(Value = "never")] Never,
    [EnumMember(Value = "on-request")] OnRequest,
    [EnumMember(Value = "on-failure")] OnFailure,
    [EnumMember(Value = "untrusted")] Untrusted,
}

public enum SandboxMode
{
    [EnumMember(Value = "read-only")] ReadOnly,
    [EnumMember(Value = "workspace-write")] WorkspaceWrite,
    [EnumMember(Value = "danger-full-access")] DangerFullAccess,
}

public enum ModelReasoningEffort
{
    [EnumMember(Value = "minimal")] Minimal,
    [EnumMember(Value = "low")] Low,
    [EnumMember(Value = "medium")] Medium,
    [EnumMember(Value = "high")] High,
    [EnumMember(Value = "xhigh")] XHigh,
}

public enum WebSearchMode
{
    [EnumMember(Value = "disabled")] Disabled,
    [EnumMember(Value = "cached")] Cached,
    [EnumMember(Value = "live")] Live,
}

public sealed class ThreadOptions
{
    public string? Model { get; init; }
    public SandboxMode? SandboxMode { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool? SkipGitRepoCheck { get; init; }
    public ModelReasoningEffort? ModelReasoningEffort { get; init; }
    public bool? NetworkAccessEnabled { get; init; }
    public WebSearchMode? WebSearchMode { get; init; }
    public bool? WebSearchEnabled { get; init; }
    public ApprovalMode? ApprovalPolicy { get; init; }
    public IReadOnlyList<string>? AdditionalDirectories { get; init; }
}
