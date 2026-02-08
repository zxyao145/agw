using DSystem.Domain.Services;
using DSystem.Shared.Models;
using Microsoft.Extensions.AI;

namespace DSystem.Manager.Api.Contracts;

public record AgentCreateRequest(
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderApiKeyId,
    string? Tools = null);  // JSON array of tool names

public record AgentUpdateRequest(
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderApiKeyId,
    string? Tools = null);  // JSON array of tool names

public record AiAgentResponse(Guid Id, string Name, string SystemPrompt, string ProviderName, string ModelName, string Endpoint, string ApiKey)
{
    public static AiAgentResponse FromDomain(AiAgent agent) =>
        new(agent.Id, agent.Name, agent.SystemPrompt, agent.ProviderName, agent.ModelName, agent.Endpoint, agent.ApiKey);
}

public record AgentExecuteRequest(string Input, string? ThreadId = null);

public record ChatMessageResponse(string Role, string Content);

public record AgentExecuteResponse(
    string ThreadId,
    IReadOnlyList<AiMessage> Messages)
{
    public static AgentExecuteResponse FromDomain(AgentExecutionResult result) =>
        new(result.ThreadId, result.Messages);
}
