using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Definitions.Contracts;

public record AgentCreateRequest(
    string DisplayName,
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null,
    List<Guid>? AppInstanceIds = null);

public record AgentUpdateRequest(
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null,
    List<Guid>? AppInstanceIds = null);

public sealed record AgentMcpToolServerRelationResponse(
    Guid AgentId,
    Guid McpToolServerId)
{
    public static AgentMcpToolServerRelationResponse FromDomain(AgentMcpServerRelation relation) =>
        new(relation.AgentId, relation.McpToolServerId);
}

public sealed record AgentSkillRelationResponse(
    Guid AgentId,
    Guid SkillId)
{
    public static AgentSkillRelationResponse FromDomain(AgentSkillRelation relation) =>
        new(relation.AgentId, relation.SkillId);
}

public sealed record AgentAppRelationResponse(
    Guid AgentId,
    Guid AppInstanceId)
{
    public static AgentAppRelationResponse FromDomain(AgentAppRelation relation) =>
        new(relation.AgentId, relation.AppInstanceId);
}

public sealed record AgentResponse(
    Guid Id,
    string DisplayName,
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools,
    AgentType Type,
    string? Extra,
    IReadOnlyList<AgentMcpToolServerRelationResponse> AgentMcpToolServers,
    IReadOnlyList<AgentSkillRelationResponse> AgentSkillRelations,
    IReadOnlyList<AgentAppRelationResponse> AgentAppRelations,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy)
{
    public static AgentResponse FromDomain(Agent agent) =>
        new(
            agent.Id,
            agent.DisplayName,
            agent.Name,
            agent.Description,
            agent.SystemPrompt,
            agent.ModelProviderId,
            agent.Tools,
            agent.Type,
            agent.Extra,
            [.. agent.AgentMcpToolServers.Select(AgentMcpToolServerRelationResponse.FromDomain)],
            [.. agent.AgentSkillRelations.Select(AgentSkillRelationResponse.FromDomain)],
            [.. agent.AgentAppRelations.Select(AgentAppRelationResponse.FromDomain)],
            agent.CreateTime,
            agent.CreateBy,
            agent.UpdateTime,
            agent.UpdateBy);
}
