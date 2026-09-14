namespace Agw.Shared.Runtime;

/// <summary>A directory in the immutable workspace configuration consumed by one execution turn.</summary>
public sealed record ProjectWorkspaceDirectory(Guid Id, string Path);

/// <summary>The immutable directory configuration consumed by one execution turn.</summary>
public sealed record ProjectWorkspaceSnapshot(
    string Workspace,
    IReadOnlyList<ProjectWorkspaceDirectory> AdditionalDirectories,
    string Fingerprint
);
