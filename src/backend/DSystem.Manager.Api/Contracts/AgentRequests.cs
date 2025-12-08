using DSystem.Domain.Models;

namespace DSystem.Manager.Api.Contracts;

public record AgentCreateRequest(string Name, string Instructions, string SystemPrompt, Guid ModelProviderApiKeyId);

public record AgentUpdateRequest(string Name, string Instructions, string SystemPrompt, Guid ModelProviderApiKeyId);

public record AiAgentResponse(Guid Id, string Name, string Instructions, string SystemPrompt, string ProviderName, string ModelName, string Endpoint, string ApiKey)
{
    public static AiAgentResponse FromDomain(AiAgent agent) =>
        new(agent.Id, agent.Name, agent.Instructions, agent.SystemPrompt, agent.ProviderName, agent.ModelName, agent.Endpoint, agent.ApiKey);
}
