using Agw.Auth.Contracts;
using Agw.Integrations.Application.Persistence;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Credentials;

public sealed class ConnectionCredentialReader : IConnectionCredentialReader
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public ConnectionCredentialReader(IIntegrationsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<ResolvedCredential?> ReadConnectionAsync(
        Guid connectionId,
        string slot,
        CancellationToken cancellationToken
    )
    {
        var userId = _userInfoService.RequiredUserId;
        var credential = await _dbContext
            .ConnectionCredentials.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ConnectionId == connectionId && item.Connection.CreateBy == userId && item.Slot == slot,
                cancellationToken
            );
        return credential == null ? null : Resolve(credential.Value, credential.ExpiresAtUtc);
    }

    public async Task<ResolvedCredential?> ReadPluginInstallationAsync(
        Guid pluginInstallationId,
        string slot,
        CancellationToken cancellationToken
    )
    {
        var credential = await _dbContext
            .PluginInstallationCredentials.AsNoTracking()
            .FirstOrDefaultAsync(
                item =>
                    item.PluginInstallationId == pluginInstallationId
                    && item.PluginInstallation!.CreateBy == _userInfoService.RequiredUserId
                    && item.Slot == slot,
                cancellationToken
            );
        return credential == null ? null : Resolve(credential.Value, null);
    }

    private ResolvedCredential Resolve(string? value, DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        }

        return new ResolvedCredential { Value = value, ExpiresAtUtc = expiresAtUtc };
    }
}
