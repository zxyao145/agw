using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.References;
using Agw.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application.Facades;

public sealed class ModelProviderReferenceFacade : IModelProviderReferenceFacade
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public ModelProviderReferenceFacade(IProvidersDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterVisibleModelProviderIdsAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(modelProviderIds);
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .ModelProviders.AsNoTracking()
            .Where(modelProvider => ids.Contains(modelProvider.Id) && modelProvider.CreateBy == ownerUserId)
            .Select(modelProvider => modelProvider.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModelProviderRuntimeSnapshot?> GetRuntimeSnapshotAsync(
        Guid modelProviderId,
        CancellationToken cancellationToken = default
    )
    {
        if (modelProviderId == Guid.Empty)
        {
            return null;
        }

        var ownerUserId = _currentUser.RequiredUserId;
        var modelProvider = await _dbContext
            .ModelProviders.AsNoTracking()
            .Include(relation => relation.Model)
            .Include(relation => relation.Provider)
                .ThenInclude(provider => provider!.AuthConfigs)
            .SingleOrDefaultAsync(
                relation => relation.Id == modelProviderId && relation.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (modelProvider?.Model is not { } model || modelProvider.Provider is not { } provider)
        {
            return null;
        }

        return new ModelProviderRuntimeSnapshot(
            modelProvider.Id,
            new ModelProviderModelSnapshot(model.Id, model.Name, model.MaxContextWindowTokens, model.MaxOutputTokens),
            new ModelProviderProviderSnapshot(
                provider.Id,
                provider.Name,
                provider.ProviderType,
                provider.Endpoint,
                provider
                    .AuthConfigs.Select(config => new ProviderAuthConfigSnapshot(config.Enable, config.ApiKey))
                    .ToArray()
            )
        );
    }
}
