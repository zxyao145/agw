using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Behaviors;
using Agw.Providers.Domain.Services;
using Agw.Providers.Domain.ValueObjects;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ProviderAppService : IProviderAppService
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ProviderModelBindingDomainService _bindingDomainService;
    private readonly ICurrentUser _currentUser;

    public ProviderAppService(
        IProvidersDbContext dbContext,
        ProviderModelBindingDomainService bindingDomainService,
        ICurrentUser currentUser
    )
    {
        _dbContext = dbContext;
        _bindingDomainService = bindingDomainService;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<Provider>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        var providers = await _dbContext
            .Providers.AsNoTracking()
            .Include(provider => provider.AuthConfigs)
            .Where(provider => provider.CreateBy == ownerUserId)
            .ToListAsync();
        return providers.OrderByDescending(provider => provider.CreateTime).ToList();
    }

    public Task<Provider?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext
            .Providers.AsNoTracking()
            .Include(provider => provider.AuthConfigs)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId);
    }

    public async Task<Provider> CreateAsync(ProviderCreateRequest request)
    {
        _ = ResolveOwnerUserId();
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint,
        };

        new ProviderBehavior(provider).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));
        var modelSelection = await _bindingDomainService.SelectModelsAsync(provider.Id, [], request.ModelNames ?? []);
        await _dbContext.Providers.AddAsync(provider);
        await ApplyModelSelectionAsync(modelSelection);
        await _dbContext.SaveChangesAsync();
        return provider;
    }

    public async Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext
            .Providers.Include(provider => provider.AuthConfigs)
            .Include(provider => provider.Models)
                .ThenInclude(modelProvider => modelProvider.Model)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId);
        if (existing == null)
        {
            return null;
        }

        var modelSelection =
            request.ModelNames == null
                ? null
                : await _bindingDomainService.SelectModelsAsync(
                    existing.Id,
                    existing.Models.ToList(),
                    request.ModelNames
                );

        existing.Name = request.Name;
        existing.ProviderType = request.ProviderType;
        existing.Description = request.Description;
        existing.Endpoint = request.Endpoint;
        new ProviderBehavior(existing).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));

        if (modelSelection != null)
        {
            await ApplyModelSelectionAsync(modelSelection);
        }

        _dbContext.Providers.Entry(existing).Property(provider => provider.Name).IsModified = true;
        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext
            .Providers.Include(provider => provider.AuthConfigs)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId);
        if (existing == null)
        {
            return false;
        }

        _dbContext.Providers.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    private static List<ProviderAuthConfig> BuildAuthConfigs(IReadOnlyList<ProviderAuthConfigRequest>? requests)
    {
        if (requests == null || requests.Count == 0)
        {
            return [];
        }

        return requests
            .Select(request =>
            {
                return new ProviderAuthConfig
                {
                    AuthType = request.AuthType,
                    ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey,
                    EnvName = null,
                    Enable = request.Enable,
                };
            })
            .ToList();
    }

    private async Task ApplyModelSelectionAsync(ProviderModelSelection selection)
    {
        _dbContext.ModelProviders.RemoveRange(selection.RemovedRelations);
        await _dbContext.Models.AddRangeAsync(selection.CreatedModels);
        await _dbContext.ModelProviders.AddRangeAsync(selection.AddedRelations);
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
