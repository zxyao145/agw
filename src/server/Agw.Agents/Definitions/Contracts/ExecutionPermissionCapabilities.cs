namespace Agw.Agents.Definitions.Contracts;

public sealed record ExecutionPermissionCapabilities(
    IReadOnlyList<AgwPermissionMode> SupportedPermissionModes,
    string? Reason = null
);
