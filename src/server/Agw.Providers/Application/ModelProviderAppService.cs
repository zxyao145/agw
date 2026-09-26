using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelProviderAppService : IModelProviderAppService
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ProviderModelBindingDomainService _bindingDomainService;
    private readonly ICurrentUser _currentUser;

    public ModelProviderAppService(
        IProvidersDbContext dbContext,
        ProviderModelBindingDomainService bindingDomainService,
        ICurrentUser currentUser
    )
    {
        _dbContext = dbContext;
        _bindingDomainService = bindingDomainService;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ModelProviderRelation>> ListAsync(Guid? modelId = null, Guid? providerId = null)
    {
        var ownerUserId = ResolveOwnerUserId();
        IReadOnlyList<ModelProviderRelation> modelProviders;
        if (!modelId.HasValue && !providerId.HasValue)
        {
            modelProviders = await _dbContext
                .ModelProviders.AsNoTracking()
                .Include(modelProvider => modelProvider.Model)
                .Include(modelProvider => modelProvider.Provider)
                .Where(relation => relation.CreateBy == ownerUserId)
                .ToListAsync();
        }
        else
        {
            modelProviders = await _dbContext
                .ModelProviders.AsNoTracking()
                .Include(modelProvider => modelProvider.Model)
                .Include(modelProvider => modelProvider.Provider)
                .Where(modelProvider =>
                    (!modelId.HasValue || modelProvider.ModelId == modelId.Value)
                    && (!providerId.HasValue || modelProvider.ProviderId == providerId.Value)
                    && modelProvider.CreateBy == ownerUserId
                )
                .ToListAsync();
        }

        return modelProviders.OrderByDescending(modelProvider => modelProvider.CreateTime).ToList();
    }

    public async Task<ModelProviderRelation?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .ModelProviders.AsNoTracking()
            .Include(modelProvider => modelProvider.Model)
            .Include(modelProvider => modelProvider.Provider)
            .FirstOrDefaultAsync(modelProvider => modelProvider.Id == id && modelProvider.CreateBy == ownerUserId);
    }

    public async Task<ModelProviderRelation> CreateAsync(ModelProviderCreateRequest request)
    {
        _ = ResolveOwnerUserId();
        await _bindingDomainService.EnsureBindableAsync(request.ProviderId, request.ModelId);

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

        await _dbContext.ModelProviders.AddAsync(entity);
        await _dbContext.SaveChangesAsync();
        return entity;
    }

    public async Task<ModelProviderRelation?> UpdateAsync(Guid id, ModelProviderUpdateRequest request)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext
            .ModelProviders.Include(entity => entity.Model)
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

        _dbContext.ModelProviders.Entry(existing).Property(relation => relation.InputPrice).IsModified = true;
        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await GetAsync(id);
        if (existing == null)
        {
            return false;
        }

        await _bindingDomainService.EnsureUnbindableAsync([existing.Id]);
        _dbContext.ModelProviders.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
