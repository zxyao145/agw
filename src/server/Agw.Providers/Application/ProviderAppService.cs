using Agw.Agents.Contracts.Catalog;
using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Behaviors;
using Agw.Providers.Domain.Services;
using Agw.Providers.Domain.ValueObjects;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ProviderAppService : IProviderAppService
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ProviderModelBindingDomainService _bindingDomainService;
    private readonly ICurrentUser _currentUser;
    private readonly IApplicationLock _applicationLock;
    private readonly IProviderModelDeletionCoordinator _deletionCoordinator;
    private readonly IAgentProviderCompatibilityFacade _agentProviderCompatibility;

    public ProviderAppService(
        IProvidersDbContext dbContext,
        ProviderModelBindingDomainService bindingDomainService,
        ICurrentUser currentUser,
        IApplicationLock applicationLock,
        IProviderModelDeletionCoordinator deletionCoordinator,
        IAgentProviderCompatibilityFacade agentProviderCompatibility
    )
    {
        _dbContext = dbContext;
        _bindingDomainService = bindingDomainService;
        _currentUser = currentUser;
        _applicationLock = applicationLock;
        _deletionCoordinator = deletionCoordinator;
        _agentProviderCompatibility = agentProviderCompatibility;
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

    public async Task<Provider> CreateAsync(
        ProviderCreateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        await using var mutationCancellation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            ownerUserId,
            cancellationToken
        );
        cancellationToken = mutationCancellation.Token;
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint,
        };

        new ProviderBehavior(provider).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));
        var modelSelection = await _bindingDomainService.SelectModelsAsync(
            provider.Id,
            [],
            request.ModelNames ?? [],
            cancellationToken
        );
        await _dbContext.Providers.AddAsync(provider, cancellationToken);
        await ApplyModelSelectionAsync(modelSelection, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return provider;
    }

    public async Task<Provider?> UpdateAsync(
        Guid id,
        ProviderUpdateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        await using var mutationCancellation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            ownerUserId,
            cancellationToken
        );
        cancellationToken = mutationCancellation.Token;
        var existing = await _dbContext
            .Providers.Include(provider => provider.AuthConfigs)
            .Include(provider => provider.Models)
                .ThenInclude(modelProvider => modelProvider.Model)
            .FirstOrDefaultAsync(provider => provider.Id == id && provider.CreateBy == ownerUserId, cancellationToken);
        if (existing == null)
        {
            return null;
        }

        await _agentProviderCompatibility.EnsureCompatibleAsync(
            existing.Models.Select(modelProvider => modelProvider.Id).ToArray(),
            request.ProviderType,
            cancellationToken
        );
        var modelSelection =
            request.ModelNames == null
                ? null
                : await _bindingDomainService.SelectModelsAsync(
                    existing.Id,
                    existing.Models.ToList(),
                    request.ModelNames,
                    cancellationToken
                );

        existing.Name = request.Name;
        existing.ProviderType = request.ProviderType;
        existing.Description = request.Description;
        existing.Endpoint = request.Endpoint;
        new ProviderBehavior(existing).ApplyAuthConfigs(BuildAuthConfigs(request.AuthConfigs));

        if (modelSelection != null)
        {
            await ApplyModelSelectionAsync(modelSelection, cancellationToken);
        }

        _dbContext.Providers.Entry(existing).Property(provider => provider.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        _deletionCoordinator.DeleteProviderAsync(id, _bindingDomainService.EnsureUnbindableAsync, cancellationToken);

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

    private async Task ApplyModelSelectionAsync(ProviderModelSelection selection, CancellationToken cancellationToken)
    {
        _dbContext.ModelProviders.RemoveRange(selection.RemovedRelations);
        await _dbContext.Models.AddRangeAsync(selection.CreatedModels, cancellationToken);
        await _dbContext.ModelProviders.AddRangeAsync(selection.AddedRelations, cancellationToken);
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
