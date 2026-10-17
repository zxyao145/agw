using Agw.Providers.Application.Persistence;
using Agw.Providers.Domain.Repositories;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Infrastructure;

public sealed class ProviderModelRepository : IProviderModelRepository
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public ProviderModelRepository(IProvidersDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public Task<bool> ProviderExistsAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = _currentUser.RequiredUserId;
        return _dbContext.Providers.AnyAsync(
            provider => provider.Id == providerId && provider.CreateBy == ownerUserId,
            cancellationToken
        );
    }

    public Task<bool> ModelExistsAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = _currentUser.RequiredUserId;
        return _dbContext.Models.AnyAsync(
            model => model.Id == modelId && model.CreateBy == ownerUserId,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<AgwAiModel>> ListModelsByNameAsync(
        IReadOnlyCollection<string> modelNames,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .Models.AsNoTracking()
            .Where(model => modelNames.Contains(model.Name) && model.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken);
    }
}
