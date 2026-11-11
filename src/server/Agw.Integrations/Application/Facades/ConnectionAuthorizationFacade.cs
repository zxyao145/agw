using System.Net.Http.Headers;
using Agw.Auth.Contracts;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Facades;

public sealed class ConnectionAuthorizationFacade : IConnectionAuthorizationFacade
{
    private readonly IIntegrationsDbContext _db;
    private readonly IConnectionCredentialReader _credentials;
    private readonly IPluginCatalog _plugins;
    private readonly OAuthRefreshAppService _refresh;
    private readonly TimeProvider _timeProvider;

    public ConnectionAuthorizationFacade(
        IIntegrationsDbContext db,
        IConnectionCredentialReader credentials,
        IPluginCatalog plugins,
        OAuthRefreshAppService refresh,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _credentials = credentials;
        _plugins = plugins;
        _refresh = refresh;
        _timeProvider = timeProvider;
    }

    public async Task<string> GetAuthorizationHeaderAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        var connection =
            await _db
                .Connections.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == connectionId && row.CreateBy == owner, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ConnectionNotFound);
        if (!connection.Enabled || connection.Status != ConnectionStatus.Ready)
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        var definition = IntegrationDefinitionResolver.Resolve(
            _plugins,
            connection.PluginId,
            connection.ConnectorId,
            connection.AuthSchemeId
        );
        if (definition.AuthScheme.Type != AuthSchemeType.OAuth2)
            throw new AgwException(
                ErrorCodes.IntegrationConfigurationInvalid,
                "This connection does not provide an OAuth authorization header."
            );
        var credential = await ReadAsync(connectionId, cancellationToken);
        if (credential.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            if (definition.AuthScheme.OAuth2AuthorizationCode?.SupportsRefresh != true)
                throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
            await _refresh.RefreshAsync(connectionId, cancellationToken);
            credential = await ReadAsync(connectionId, cancellationToken);
        }
        if (
            credential.ExpiresAtUtc <= _timeProvider.GetUtcNow()
            || !AuthenticationHeaderValue.TryParse("Bearer " + credential.Value, out var header)
        )
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        return header.ToString();
    }

    private async Task<ResolvedCredential> ReadAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await _credentials.ReadConnectionAsync(
            connectionId,
            IntegrationCredentialSlots.OAuthAccessToken,
            cancellationToken
        ) ?? throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
}
