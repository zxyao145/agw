using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ProviderAppService : IProviderAppService
{
    private readonly IRepository<Provider> _providerRepository;
    private readonly IRepository<AgwAiModel> _modelRepository;
    private readonly IRepository<ModelProviderRelation> _modelProviderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProviderDomainService _providerDomainService;
    private readonly ModelDomainService _modelDomainService;
    private readonly ModelProviderDomainService _modelProviderDomainService;
    private readonly ModelProviderUsageGuard _modelProviderUsageGuard;
    private readonly ICurrentUser _currentUser;

    public ProviderAppService(
        IRepository<Provider> providerRepository,
        IRepository<AgwAiModel> modelRepository,
        IRepository<ModelProviderRelation> modelProviderRepository,
        IUnitOfWork unitOfWork,
        ProviderDomainService providerDomainService,
        ModelDomainService modelDomainService,
        ModelProviderDomainService modelProviderDomainService,
        ModelProviderUsageGuard modelProviderUsageGuard,
        ICurrentUser currentUser
    )
    {
        _providerRepository = providerRepository;
        _modelRepository = modelRepository;
        _modelProviderRepository = modelProviderRepository;
        _unitOfWork = unitOfWork;
        _providerDomainService = providerDomainService;
        _modelDomainService = modelDomainService;
        _modelProviderDomainService = modelProviderDomainService;
        _modelProviderUsageGuard = modelProviderUsageGuard;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<Provider>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        var providers = await _providerRepository.ListAsync(
            provider => provider.CreateBy == ownerUserId,
            null,
            provider => provider.AuthConfigs
        );
        return providers.OrderByDescending(provider => provider.CreateTime).ToList();
    }

    public Task<Provider?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _providerRepository
            .Queryable.Include(provider => provider.AuthConfigs)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId);
    }

    public async Task<Provider> CreateAsync(ProviderCreateRequest request, string user)
    {
        var provider = new Provider
        {
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint,
            AuthConfigs = BuildAuthConfigs(request.AuthConfigs),
        };

        _providerDomainService.PrepareForCreate(provider, user);
        await _providerRepository.AddAsync(provider);
        await SyncModelRelationsAsync(provider.Id, [], NormalizeModelNames(request.ModelNames) ?? [], user);
        await _unitOfWork.SaveChangesAsync();
        return provider;
    }

    public async Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _providerRepository
            .Queryable.Include(provider => provider.AuthConfigs)
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

        _providerDomainService.ApplyUpdate(
            existing,
            provider =>
            {
                provider.Name = request.Name;
                provider.ProviderType = request.ProviderType;
                provider.Description = request.Description;
                provider.Endpoint = request.Endpoint;

                if (provider.AuthConfigs == null)
                {
                    provider.AuthConfigs = new List<ProviderAuthConfig>();
                }

                provider.AuthConfigs.Clear();
                foreach (var authConfig in BuildAuthConfigs(request.AuthConfigs))
                {
                    provider.AuthConfigs.Add(authConfig);
                }
            },
            user
        );

        if (normalizedModelNames != null)
        {
            await SyncModelRelationsAsync(existing.Id, existing.Models.ToList(), normalizedModelNames, user);
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _providerRepository
            .Queryable.Include(provider => provider.AuthConfigs)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId);
        if (existing == null)
        {
            return false;
        }

        _providerRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
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
            _modelProviderRepository.Remove(relation);
        }

        var models =
            modelNames.Count == 0
                ? []
                : await _modelRepository
                    .Queryable.Where(model => modelNames.Contains(model.Name) && model.CreateBy == user)
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
                Name = modelName,
                Description = null,
                MaxContextWindowTokens = AgwAiModel.DefaultMaxContextWindowTokens,
                MaxOutputTokens = AgwAiModel.DefaultMaxOutputTokens,
            };
            _modelDomainService.PrepareForCreate(model, user);
            await _modelRepository.AddAsync(model);
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

            var relation = new ModelProviderRelation { ProviderId = providerId, ModelId = model.Id };
            _modelProviderDomainService.PrepareForCreate(relation, user);
            await _modelProviderRepository.AddAsync(relation);
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
