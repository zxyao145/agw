using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;

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
        var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId);
        if (apiKey == null)
        {
            return null;
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
