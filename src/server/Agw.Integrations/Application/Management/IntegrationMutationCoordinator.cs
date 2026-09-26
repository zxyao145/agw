using Agw.Auth.Contracts;
using Agw.Integrations.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

/// <summary>Serializes a user's plugin configuration and credential mutations across hosts.</summary>
public sealed class IntegrationMutationCoordinator
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IUserInfoService _userInfo;

    public IntegrationMutationCoordinator(
        IIntegrationsDbContext dbContext,
        IApplicationLock applicationLock,
        IUserInfoService userInfo
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _userInfo = userInfo;
    }

    public async Task<MutationScope> AcquireConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var user = _userInfo.RequiredUserId;
        var pluginId =
            await _dbContext
                .Connections.AsNoTracking()
                .Where(item => item.Id == connectionId && item.CreateBy == user)
                .Select(item => item.PluginId)
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.ConnectionNotFound);
        return await AcquirePluginAsync(pluginId, cancellationToken);
    }

    public async Task<MutationScope> AcquirePluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        var user = _userInfo.RequiredUserId;
        var lease = await _applicationLock.AcquireAsync(
            $"integration:{user.Length}:{user}:{pluginId}",
            cancellationToken
        );
        try
        {
            // A reused scope may have read these rows before waiting for another host.
            DetachUnchanged(_dbContext.ConnectionCredentials);
            DetachUnchanged(_dbContext.Connections);
            DetachUnchanged(_dbContext.PluginInstallationCredentials);
            DetachUnchanged(_dbContext.PluginInstallations);
            return new MutationScope(lease, cancellationToken);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private static void DetachUnchanged<TEntity>(DbSet<TEntity> entities)
        where TEntity : class
    {
        foreach (var entity in entities.Local.ToArray())
        {
            var entry = entities.Entry(entity);
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Detached;
        }
    }

    public sealed class MutationScope : IAsyncDisposable
    {
        private readonly IApplicationLockLease _lease;
        private readonly CancellationTokenSource _cancellation;

        public MutationScope(IApplicationLockLease lease, CancellationToken cancellationToken)
        {
            _lease = lease;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        }

        public CancellationToken Token => _cancellation.Token;

        public async ValueTask DisposeAsync()
        {
            _cancellation.Dispose();
            await _lease.DisposeAsync();
        }
    }
}
