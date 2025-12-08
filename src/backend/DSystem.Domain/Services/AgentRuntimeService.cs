using DSystem.Domain.Entities;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;

namespace DSystem.Domain.Services;

/// <summary>
/// Shapes persisted Agent data plus its Model/Provider/API key into a runtime payload
/// consumable by MicrosoftAgentFramework when creating an <see cref="AiAgent"/>.
/// </summary>
public class AgentRuntimeService
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<ModelProviderApiKey> _apiKeyRepository;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;

    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IRepository<ModelProviderApiKey> apiKeyRepository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository)
    {
        _agentRepository = agentRepository;
        _apiKeyRepository = apiKeyRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
    }

    public async Task<AiAgent?> CreateAiAgentAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId);
        if (apiKey == null || !apiKey.Enable)
        {
            return null;
        }

        var modelProvider = await _modelProviderRepository.GetByIdAsync(new object[] { apiKey.ModelId, apiKey.ProviderId });
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.GetByIdAsync(apiKey.ModelId);
        var provider = await _providerRepository.GetByIdAsync(apiKey.ProviderId);
        if (model == null || provider == null)
        {
            return null;
        }

        return new AiAgent
        {
            Id = agent.Id,
            Name = agent.Name,
            Instructions = agent.Instructions,
            SystemPrompt = agent.SystemPrompt,
            ProviderName = provider.Name,
            ModelName = model.Name,
            Endpoint = provider.Endpoint,
            ApiKey = apiKey.ApiKey
        };
    }
}
