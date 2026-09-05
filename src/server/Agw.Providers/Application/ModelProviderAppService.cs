using Agw.Providers.Contracts.Manager;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelProviderAppService : IModelProviderAppService
{
    private readonly IRepository<ModelProviderRelation> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ModelProviderUsageGuard _usageGuard;
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<Provider> _providerRepository;
    private readonly IRepository<AgwAiModel> _modelRepository;

    public ModelProviderAppService(
        IRepository<ModelProviderRelation> repository,
        IUnitOfWork unitOfWork,
        ModelProviderUsageGuard usageGuard,
        ICurrentUser currentUser,
        IRepository<Provider> providerRepository,
        IRepository<AgwAiModel> modelRepository
    )
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _usageGuard = usageGuard;
        _currentUser = currentUser;
        _providerRepository = providerRepository;
        _modelRepository = modelRepository;
    }

    public async Task<IReadOnlyList<ModelProviderRelation>> ListAsync(Guid? modelId = null, Guid? providerId = null)
    {
        var ownerUserId = ResolveOwnerUserId();
        IReadOnlyList<ModelProviderRelation> modelProviders;
        if (!modelId.HasValue && !providerId.HasValue)
        {
            modelProviders = await _repository.ListAsync(
                relation => relation.CreateBy == ownerUserId,
                null,
                includes: [modelProvider => modelProvider.Model!, modelProvider => modelProvider.Provider!]
            );
        }
        else
        {
            modelProviders = await _repository.ListAsync(
                modelProvider =>
                    (!modelId.HasValue || modelProvider.ModelId == modelId.Value)
                    && (!providerId.HasValue || modelProvider.ProviderId == providerId.Value)
                    && modelProvider.CreateBy == ownerUserId,
                null,
                modelProvider => modelProvider.Model!,
                modelProvider => modelProvider.Provider!
            );
        }

        return modelProviders.OrderByDescending(modelProvider => modelProvider.CreateTime).ToList();
    }

    public async Task<ModelProviderRelation?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var results = await _repository.ListAsync(
            modelProvider => modelProvider.Id == id && modelProvider.CreateBy == ownerUserId,
            null,
            modelProvider => modelProvider.Model!,
            modelProvider => modelProvider.Provider!
        );
        return results.Count > 0 ? results[0] : null;
    }

    public async Task<ModelProviderRelation> CreateAsync(ModelProviderCreateRequest request, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var providerExists = await _providerRepository.Queryable.AnyAsync(provider =>
            provider.Id == request.ProviderId && provider.CreateBy == ownerUserId
        );
        var modelExists = await _modelRepository.Queryable.AnyAsync(model =>
            model.Id == request.ModelId && model.CreateBy == ownerUserId
        );
        if (!providerExists || !modelExists)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var entity = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = request.ModelId,
            ProviderId = request.ProviderId,
            InputPrice = request.InputPrice,
            OutputPrice = request.OutputPrice,
            CacheRead = request.CacheRead,
            CacheWrite = request.CacheWrite,
            RpsLimit = request.RpsLimit,
        };

        await _repository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<ModelProviderRelation?> UpdateAsync(Guid id, ModelProviderUpdateRequest request, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _repository
            .Queryable.Include(entity => entity.Model)
            .Include(entity => entity.Provider)
            .FirstOrDefaultAsync(entity => entity.Id == id && entity.CreateBy == ownerUserId);
        if (existing == null)
        {
            return null;
        }

        existing.InputPrice = request.InputPrice;
        existing.OutputPrice = request.OutputPrice;
        existing.CacheRead = request.CacheRead;
        existing.CacheWrite = request.CacheWrite;
        existing.RpsLimit = request.RpsLimit;

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

        await _usageGuard.EnsureNotInUseAsync([existing.Id]);
        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
