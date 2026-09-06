using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Contracts.References;
using Agw.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Facades;

public sealed class ConnectionReferenceFacade : IConnectionReferenceFacade
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public ConnectionReferenceFacade(IIntegrationsDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterOwnedConnectionIdsAsync(
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        var ids = connectionIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .Connections.AsNoTracking()
            .Where(connection => ids.Contains(connection.Id) && connection.CreateBy == ownerUserId)
            .Select(connection => connection.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
