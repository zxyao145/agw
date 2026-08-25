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
