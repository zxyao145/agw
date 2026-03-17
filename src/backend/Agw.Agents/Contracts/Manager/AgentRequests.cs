using Agw.Appliaction.Services;
using Agw.Shared.Models;
using Microsoft.Extensions.AI;

namespace Agw.Manager.Api.Contracts;

public record AgentCreateRequest(
    string DisplayName,
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null);

public record AgentUpdateRequest(
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    string? Tools = null,  // JSON array of tool names
    List<Guid>? McpToolServerIds = null);

public record AiAgentResponse(Guid Id, string Name, string SystemPrompt, string ProviderName, string ModelName, string Endpoint, string ApiKey)
{
    public static AiAgentResponse FromDomain(AiAgent agent) =>
        new(agent.Id, agent.Name, agent.SystemPrompt, agent.ProviderName, agent.ModelName, agent.Endpoint, agent.ApiKey);
}

public record AgentExecuteRequest(string Input, string? SessionId = null, string? ProjectId = null);

public record ChatMessageResponse(string Role, string Content);

public record AgentExecuteResponse(
    string SessionId,
    IReadOnlyList<AiMessage> Messages)
{
    public static AgentExecuteResponse FromDomain(AgentExecutionResult result) =>
        new(result.SessionId, result.Messages);
}
