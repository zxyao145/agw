using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public record ProjectCreateRequest(
    string Name,
    string? Description,
    string? Workspace,
    bool Enable,
    string? ExtraSetting,
    string? Tools = null,
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null,
    List<Guid>? AppInstanceIds = null,
    Dictionary<string, string>? EnvironmentVariables = null);

public record ProjectUpdateRequest(
    string Name,
    string? Description,
    string? Workspace,
    bool Enable,
    string? ExtraSetting,
    string? Tools = null,
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null,
    List<Guid>? AppInstanceIds = null,
    Dictionary<string, string>? EnvironmentVariables = null);

public sealed record ProjectMcpToolServerRelationResponse(
    Guid ProjectId,
    Guid McpToolServerId)
{
    public static ProjectMcpToolServerRelationResponse FromDomain(ProjectMcpServerRelation relation) =>
        new(relation.ProjectId, relation.McpToolServerId);
}

public sealed record ProjectSkillRelationResponse(
    Guid ProjectId,
    Guid SkillId)
{
    public static ProjectSkillRelationResponse FromDomain(ProjectSkillRelation relation) =>
        new(relation.ProjectId, relation.SkillId);
}

public sealed record ProjectAppRelationResponse(
    Guid ProjectId,
    Guid AppInstanceId)
{
    public static ProjectAppRelationResponse FromDomain(ProjectAppRelation relation) =>
        new(relation.ProjectId, relation.AppInstanceId);
}

public sealed record ProjectResponse(
    Guid Id,
    string Name,
    ProjectType Type,
    string? Description,
    string? Workspace,
    bool Enable,
    string? ExtraSetting,
    string? Tools,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<ProjectMcpToolServerRelationResponse> ProjectMcpToolServers,
    IReadOnlyList<ProjectSkillRelationResponse> ProjectSkillRelations,
    IReadOnlyList<ProjectAppRelationResponse> ProjectAppRelations,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy)
{
    public static ProjectResponse FromDomain(Project project) =>
        new(
            project.Id,
            project.Name,
            project.Type,
            project.Description,
            project.Workspace,
            project.Enable,
            project.ExtraSetting,
            project.Tools,
            project.EnvironmentVariables ?? new Dictionary<string, string>(),
            [.. project.ProjectMcpToolServers.Select(ProjectMcpToolServerRelationResponse.FromDomain)],
            [.. project.ProjectSkillRelations.Select(ProjectSkillRelationResponse.FromDomain)],
            [.. project.ProjectAppRelations.Select(ProjectAppRelationResponse.FromDomain)],
            project.CreateTime,
            project.CreateBy,
            project.UpdateTime,
            project.UpdateBy);
}
