using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Providers.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Providers;

public sealed class ProviderModelDeletionCoordinator : IProviderModelDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IUserInfoService _userInfoService;

    public ProviderModelDeletionCoordinator(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _userInfoService = userInfoService;
    }

    public Task<bool> DeleteProviderAsync(
        Guid providerId,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> ensureUnbindableAsync,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return ExecuteAsync(
            ownerUserId,
            async token =>
            {
                var providers = _dbContext.Providers.Where(provider =>
                    provider.Id == providerId && provider.CreateBy == ownerUserId
                );
                if (!await providers.AnyAsync(token))
                {
                    return false;
                }

                var relations = _dbContext.ModelProviders.Where(relation =>
                    relation.ProviderId == providerId && relation.CreateBy == ownerUserId
                );
                var relationIds = await relations.Select(relation => relation.Id).ToListAsync(token);
                await ensureUnbindableAsync(relationIds, token);

                await relations.ExecuteDeleteAsync(token);
                await _dbContext
                    .ProviderAuthConfigs.Where(config =>
                        config.ProviderId == providerId && config.CreateBy == ownerUserId
                    )
                    .ExecuteDeleteAsync(token);
                await providers.ExecuteDeleteAsync(token);
                return true;
            },
            cancellationToken
        );
    }

    public Task<bool> DeleteModelAsync(
        Guid modelId,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> ensureUnbindableAsync,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return ExecuteAsync(
            ownerUserId,
            async token =>
            {
                var models = _dbContext.Models.Where(model => model.Id == modelId && model.CreateBy == ownerUserId);
                if (!await models.AnyAsync(token))
                {
                    return false;
                }

                var relations = _dbContext.ModelProviders.Where(relation =>
                    relation.ModelId == modelId && relation.CreateBy == ownerUserId
                );
                var relationIds = await relations.Select(relation => relation.Id).ToListAsync(token);
                await ensureUnbindableAsync(relationIds, token);

                await relations.ExecuteDeleteAsync(token);
                await models.ExecuteDeleteAsync(token);
                return true;
            },
            cancellationToken
        );
    }

    private async Task<bool> ExecuteAsync(
        string ownerUserId,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken
    )
    {
        await using var mutation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            ownerUserId,
            cancellationToken
        );
        cancellationToken = mutation.Token;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await operation(cancellationToken);
        if (deleted)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return deleted;
    }
}
