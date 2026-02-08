using DSystem.Domain.Entities;
using DSystem.Shared.Repositories;
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

    public async Task<ModelProvider> CreateAsync(ModelProvider entity, string user)
    {
        entity.Id = Guid.NewGuid();
        entity.CreateBy = user;
        entity.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<ModelProvider?> UpdateAsync(Guid id, Action<ModelProvider> updateAction, string user)
    {
        var existing = await GetAsync(id);
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
        var existing = await GetAsync(id);
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

    public Task<IReadOnlyList<ModelProvider>> ListWithDetailsAsync(Expression<Func<ModelProvider, bool>>? predicate = null) =>
        _repository.ListAsync(predicate, mp => mp.Model!, mp => mp.Provider!);

    public async Task<ModelProvider?> GetAsync(Guid id)
    {
        var results = await _repository.ListAsync(x => x.Id == id);
        return results.Count > 0 ? results[0] : null;
    }
}
