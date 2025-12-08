using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class ModelProviderApiKeyDomainService
{
    private readonly IRepository<ModelProviderApiKey> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ModelProviderApiKeyDomainService(IRepository<ModelProviderApiKey> repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<ModelProviderApiKey> CreateAsync(ModelProviderApiKey apiKey, string user)
    {
        apiKey.Id = apiKey.Id == Guid.Empty ? Guid.NewGuid() : apiKey.Id;
        apiKey.CreateBy = user;
        apiKey.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(apiKey);
        await _unitOfWork.SaveChangesAsync();
        return apiKey;
    }

    public async Task<ModelProviderApiKey?> UpdateAsync(Guid id, Action<ModelProviderApiKey> updateAction, string user)
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

    public Task<IReadOnlyList<ModelProviderApiKey>> ListAsync(Expression<Func<ModelProviderApiKey, bool>>? predicate = null) =>
        _repository.ListAsync(predicate);

    public Task<ModelProviderApiKey?> GetAsync(Guid id) => _repository.GetByIdAsync(id);
}
