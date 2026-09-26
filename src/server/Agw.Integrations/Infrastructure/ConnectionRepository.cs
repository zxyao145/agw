using Agw.Auth.Contracts;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Domain.Repositories;
using Agw.Shared.Data.Entities.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Infrastructure;

public sealed class ConnectionRepository : IConnectionRepository
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public ConnectionRepository(IIntegrationsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public Task<bool> AliasExistsAsync(string alias, CancellationToken cancellationToken)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return _dbContext.Connections.AnyAsync(
            connection => connection.CreateBy == ownerUserId && connection.Alias == alias,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<Connection>> ListTrackedForPluginAsync(
        string pluginId,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return await _dbContext
            .Connections.Include(connection => connection.Credentials)
            .Where(connection => connection.PluginId == pluginId && connection.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken);
    }
}
