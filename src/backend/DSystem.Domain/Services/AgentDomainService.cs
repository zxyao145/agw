using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class AgentDomainService
{
    private readonly IRepository<Agent> _repository;
    private readonly IRepository<ModelProviderApiKey> _apiKeyRepository;
    private readonly IUnitOfWork _unitOfWork;

    public AgentDomainService(
        IRepository<Agent> repository,
        IRepository<ModelProviderApiKey> apiKeyRepository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _apiKeyRepository = apiKeyRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Agent?> CreateAsync(Agent agent, string user)
    {
        // Validate ModelProviderApiKeyId based on AgentType
        if (agent.Type == AgentType.System)
        {
            // System agents require a valid ModelProviderApiKeyId
            if (!agent.ModelProviderApiKeyId.HasValue)
            {
                throw new InvalidOperationException("System agents must have a ModelProviderApiKeyId.");
            }

            var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId.Value);
            if (apiKey == null)
            {
                return null;
            }
        }
        else if (agent.Type == AgentType.External)
        {
            // External agents can have optional ModelProviderApiKeyId
            if (agent.ModelProviderApiKeyId.HasValue)
            {
                var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId.Value);
                if (apiKey == null)
                {
                    return null;
                }
            }
        }

        agent.Id = agent.Id == Guid.Empty ? Guid.NewGuid() : agent.Id;
        agent.CreateBy = user;
        agent.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(agent);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAsync(Guid id, Action<Agent> updateAction, string user)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _repository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<Agent>> ListAsync(Expression<Func<Agent, bool>>? predicate = null) =>
        _repository.ListAsync(predicate);

    public Task<Agent?> GetAsync(Guid id) => _repository.GetByIdAsync(id);
}
