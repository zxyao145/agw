using Agw.Appliaction.Services.Agents;
using Agw.Shared.Models;

namespace Agw.Manager.Api.Contracts;

public record AgentCreateRequest(
    string DisplayName,
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null);

public record AgentUpdateRequest(
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null);

public record AiAgentResponse(Guid Id, string Name, string SystemPrompt, string ProviderName, string ModelName, string Endpoint, string ApiKey)
{
    public static AiAgentResponse FromDomain(AgentDefinitionDto agent) =>
        new(agent.Id, agent.Name, agent.SystemPrompt, agent.ProviderName, agent.ModelName, agent.Endpoint, agent.ApiKey);
}

public record AgentExecuteResponse(
    string TaskId,
    IReadOnlyList<AgwMessage> Messages)
{
    public static AgentExecuteResponse FromDomain(AgentExecutionResult result) =>
        new(result.TaskId, result.Messages);
}
