namespace Agw.Agents.Definitions.Contracts;

public sealed record ExecutionPermissionCapabilities(
    IReadOnlyList<AgwPermissionMode> SupportedPermissionModes,
    bool SupportsNoTools,
    string? Reason = null
);
