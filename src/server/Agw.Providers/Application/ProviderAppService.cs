using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Behaviors;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ProviderAppService : IProviderAppService
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ModelProviderUsageGuard _modelProviderUsageGuard;
    private readonly ICurrentUser _currentUser;

    public ProviderAppService(
        IProvidersDbContext dbContext,
        ModelProviderUsageGuard modelProviderUsageGuard,
        ICurrentUser currentUser
    )
    {
        _dbContext = dbContext;
        _modelProviderUsageGuard = modelProviderUsageGuard;
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

    public async Task<Provider> CreateAsync(ProviderCreateRequest request, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint,
        };

        new ProviderBehavior(provider).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));
        await _dbContext.Providers.AddAsync(provider);
        await SyncModelRelationsAsync(provider.Id, [], NormalizeModelNames(request.ModelNames) ?? [], ownerUserId);
        await _dbContext.SaveChangesAsync();
        return provider;
    }

    public async Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request, string user)
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

        var normalizedModelNames = NormalizeModelNames(request.ModelNames);
        if (normalizedModelNames != null)
        {
            var selectedNames = normalizedModelNames.ToHashSet(StringComparer.Ordinal);
            var removedRelations = existing
                .Models.Where(relation => relation.Model == null || !selectedNames.Contains(relation.Model.Name))
                .ToList();
            await _modelProviderUsageGuard.EnsureNotInUseAsync(removedRelations.Select(relation => relation.Id));
        }

        existing.Name = request.Name;
        existing.ProviderType = request.ProviderType;
        existing.Description = request.Description;
        existing.Endpoint = request.Endpoint;
        new ProviderBehavior(existing).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));

        if (normalizedModelNames != null)
        {
            await SyncModelRelationsAsync(existing.Id, existing.Models.ToList(), normalizedModelNames, ownerUserId);
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

    private async Task SyncModelRelationsAsync(
        Guid providerId,
        IReadOnlyCollection<ModelProviderRelation> currentRelations,
        IReadOnlyList<string> modelNames,
        string user
    )
    {
        var selectedNames = modelNames.ToHashSet(StringComparer.Ordinal);
        var removedRelations = currentRelations
            .Where(relation => relation.Model == null || !selectedNames.Contains(relation.Model.Name))
            .ToList();
        foreach (var relation in removedRelations)
        {
            _dbContext.ModelProviders.Remove(relation);
        }

        var models =
            modelNames.Count == 0
                ? []
                : await _dbContext
                    .Models.AsNoTracking()
                    .Where(model => modelNames.Contains(model.Name) && model.CreateBy == user)
                    .ToListAsync();
        var modelByName = models.ToDictionary(model => model.Name, StringComparer.Ordinal);
        foreach (var modelName in modelNames)
        {
            if (modelByName.ContainsKey(modelName))
            {
                continue;
            }

            var model = new AgwAiModel
            {
                Id = Guid.CreateVersion7(),
                Name = modelName,
                Description = null,
                MaxContextWindowTokens = AgwAiModel.DefaultMaxContextWindowTokens,
                MaxOutputTokens = AgwAiModel.DefaultMaxOutputTokens,
            };
            await _dbContext.Models.AddAsync(model);
            modelByName.Add(modelName, model);
        }

        var currentModelIds = currentRelations
            .Except(removedRelations)
            .Select(relation => relation.ModelId)
            .ToHashSet();
        foreach (var modelName in modelNames)
        {
            var model = modelByName[modelName];
            if (!currentModelIds.Add(model.Id))
            {
                continue;
            }

            var relation = new ModelProviderRelation
            {
                Id = Guid.CreateVersion7(),
                ProviderId = providerId,
                ModelId = model.Id,
            };
            await _dbContext.ModelProviders.AddAsync(relation);
        }
    }

    private static IReadOnlyList<string>? NormalizeModelNames(IReadOnlyList<string>? modelNames)
    {
        if (modelNames == null)
        {
            return null;
        }

        return modelNames
            .Select(modelName => modelName?.Trim())
            .Where(modelName => !string.IsNullOrEmpty(modelName))
            .Select(modelName => modelName!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
