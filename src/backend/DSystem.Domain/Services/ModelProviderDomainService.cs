using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class ModelProviderDomainService
{
    private readonly IRepository<ModelProvider> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ModelProviderDomainService(IRepository<ModelProvider> repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<ModelProvider> CreateAsync(ModelProvider link, string user)
    {
        link.CreateBy = user;
        link.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(link);
        await _unitOfWork.SaveChangesAsync();
        return link;
    }

    public async Task<ModelProvider?> UpdateAsync(Guid modelId, Guid providerId, Action<ModelProvider> updateAction, string user)
    {
        var existing = await GetAsync(modelId, providerId);
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

    public async Task<bool> DeleteAsync(Guid modelId, Guid providerId)
    {
        var existing = await GetAsync(modelId, providerId);
        if (existing == null)
        {
            return false;
        }

        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<ModelProvider>> ListAsync(Expression<Func<ModelProvider, bool>>? predicate = null) =>
        _repository.ListAsync(predicate);

    public async Task<ModelProvider?> GetAsync(Guid modelId, Guid providerId)
    {
        var results = await _repository.ListAsync(x => x.ModelId == modelId && x.ProviderId == providerId);
        return results.Count > 0 ? results[0] : null;
    }
}
