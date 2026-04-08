using Agw.Domain.Entities;
using Agw.Domain.Services;
using Agw.Manager.Api.Contracts;
using Agw.Shared.Abstractions.Repositories;

namespace Agw.Providers.Application;

public class ModelProviderAppService : IModelProviderAppService
{
    private readonly IRepository<ModelProviderRelation> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ModelProviderDomainService _domainService;

    public ModelProviderAppService(
        IRepository<ModelProviderRelation> repository,
        IUnitOfWork unitOfWork,
        ModelProviderDomainService domainService)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _domainService = domainService;
    }

    public Task<IReadOnlyList<ModelProviderRelation>> ListAsync(Guid? modelId = null, Guid? providerId = null)
    {
        if (!modelId.HasValue && !providerId.HasValue)
        {
            return _repository.ListAsync(null, modelProvider => modelProvider.OrderByDescending(x => x.CreateTime), modelProvider => modelProvider.Model!, modelProvider => modelProvider.Provider!);
        }

        return _repository.ListAsync(
            modelProvider =>
                (!modelId.HasValue || modelProvider.ModelId == modelId.Value) &&
                (!providerId.HasValue || modelProvider.ProviderId == providerId.Value),
            modelProvider => modelProvider.OrderByDescending(x => x.CreateTime),
            modelProvider => modelProvider.Model!,
            modelProvider => modelProvider.Provider!);
    }

    public async Task<ModelProviderRelation?> GetAsync(Guid id)
    {
        var results = await _repository.ListAsync(modelProvider => modelProvider.Id == id);
        return results.Count > 0 ? results[0] : null;
    }

    public async Task<ModelProviderRelation> CreateAsync(ModelProviderCreateRequest request, string user)
    {
        var entity = new ModelProviderRelation
        {
            ModelId = request.ModelId,
            ProviderId = request.ProviderId,
            InputPrice = request.InputPrice,
            OutputPrice = request.OutputPrice,
            CacheRead = request.CacheRead,
            CacheWrite = request.CacheWrite,
            RpsLimit = request.RpsLimit
        };

        _domainService.PrepareForCreate(entity, user);
        await _repository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<ModelProviderRelation?> UpdateAsync(Guid id, ModelProviderUpdateRequest request, string user)
    {
        var existing = await GetAsync(id);
        if (existing == null)
        {
            return null;
        }

        _domainService.ApplyUpdate(existing, entity =>
        {
            entity.InputPrice = request.InputPrice;
            entity.OutputPrice = request.OutputPrice;
            entity.CacheRead = request.CacheRead;
            entity.CacheWrite = request.CacheWrite;
            entity.RpsLimit = request.RpsLimit;
        }, user);

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
}
