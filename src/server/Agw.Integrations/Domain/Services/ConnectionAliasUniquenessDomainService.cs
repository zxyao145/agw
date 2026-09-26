using Agw.Integrations.Domain.Repositories;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Domain.Services;

/// <summary>
/// <para>同一所有者的连接别名唯一。</para>
/// <para>An owner's connection aliases are unique.</para>
/// </summary>
public sealed class ConnectionAliasUniquenessDomainService
{
    private readonly IConnectionRepository _connections;

    public ConnectionAliasUniquenessDomainService(IConnectionRepository connections)
    {
        _connections = connections;
    }

    public async Task EnsureAliasAvailableAsync(string normalizedAlias, CancellationToken cancellationToken)
    {
        if (await _connections.AliasExistsAsync(normalizedAlias, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ConnectionAliasAlreadyExists);
        }
    }
}
