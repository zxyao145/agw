using Agw.Shared.Tooling;

namespace Agw.Projects.Contracts.Runtime;

public sealed record ProjectRuntimeSnapshot(
    Guid Id,
    string Name,
    string? Workspace,
    string? ExtraSetting,
    IReadOnlyList<ToolValueObject> Tools,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<Guid> SkillIds,
    IReadOnlyList<Guid> McpServerIds,
    IReadOnlyList<Guid> ConnectionIds
);

public interface IProjectRuntimeFacade
{
    Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the protected per-user projects used when a caller omits an
/// explicit project or invokes the A2A protocol.
/// </summary>
public interface IProjectDefaultResolver
{
    Task<Guid?> ResolveDefaultProjectIdAsync(CancellationToken cancellationToken = default);

    Task<Guid?> ResolveA2AProjectIdAsync(CancellationToken cancellationToken = default);
}

public interface IProjectOwnershipFacade
{
    Task<IReadOnlySet<Guid>> ListOwnedProjectIdsAsync(CancellationToken cancellationToken = default);

    Task<bool> IsOwnedAsync(Guid projectId, CancellationToken cancellationToken = default);
}
