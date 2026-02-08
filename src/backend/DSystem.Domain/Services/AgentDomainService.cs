using DSystem.Domain.Entities;
using DSystem.Shared.Enums;
using DSystem.Shared.Repositories;
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

        // For External agents, only allow Description, Extra, and ModelProviderApiKeyId to be modified
        if (existing.Type == AgentType.External)
        {
            // Store original values of non-editable fields
            var originalId = existing.Id;
            var originalName = existing.Name;
            var originalSystemPrompt = existing.SystemPrompt;
            var originalTools = existing.Tools;
            var originalType = existing.Type;

            updateAction(existing);

            // Restore non-editable fields
            existing.Id = originalId;
            existing.Name = originalName;
            existing.SystemPrompt = originalSystemPrompt;
            existing.Tools = originalTools;
            existing.Type = originalType;

            // Validate ModelProviderApiKeyId if it was changed
            if (existing.ModelProviderApiKeyId.HasValue)
            {
                var apiKey = await _apiKeyRepository.GetByIdAsync(existing.ModelProviderApiKeyId.Value);
                if (apiKey == null)
                {
                    return null;
                }
            }
        }
        else
        {
            updateAction(existing);

            // Validate ModelProviderApiKeyId for System agents (required)
            if (existing.Type == AgentType.System)
            {
                if (!existing.ModelProviderApiKeyId.HasValue)
                {
                    throw new InvalidOperationException("System agents must have a ModelProviderApiKeyId.");
                }

                var apiKey = await _apiKeyRepository.GetByIdAsync(existing.ModelProviderApiKeyId.Value);
                if (apiKey == null)
                {
                    return null;
                }
            }
        }

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
