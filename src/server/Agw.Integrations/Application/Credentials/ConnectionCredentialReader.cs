using System.Security.Cryptography;

using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Credentials;

public sealed class ConnectionCredentialReader : IConnectionCredentialReader
{
    private readonly IRepository<PluginInstallationCredential> _installationCredentialRepository;
    private readonly IRepository<ConnectionCredential> _connectionCredentialRepository;
    private readonly IConnectionCredentialProtector _protector;

    public ConnectionCredentialReader(
        IRepository<PluginInstallationCredential> installationCredentialRepository,
        IRepository<ConnectionCredential> connectionCredentialRepository,
        IConnectionCredentialProtector protector)
    {
        _installationCredentialRepository = installationCredentialRepository;
        _connectionCredentialRepository = connectionCredentialRepository;
        _protector = protector;
    }

    public async Task<ResolvedCredential?> ReadConnectionAsync(
        Guid connectionId,
        string slot,
        CancellationToken cancellationToken)
    {
        var credential = await _connectionCredentialRepository.Queryable.FirstOrDefaultAsync(
            item => item.ConnectionId == connectionId && item.Slot == slot,
            cancellationToken);
        return credential == null
            ? null
            : Resolve(credential.ProtectedValue, credential.ExpiresAtUtc);
    }

    public async Task<ResolvedCredential?> ReadPluginInstallationAsync(
        Guid pluginInstallationId,
        string slot,
        CancellationToken cancellationToken)
    {
        var credential = await _installationCredentialRepository.Queryable.FirstOrDefaultAsync(
            item => item.PluginInstallationId == pluginInstallationId && item.Slot == slot,
            cancellationToken);
        return credential == null
            ? null
            : Resolve(credential.ProtectedValue, null);
    }

    private ResolvedCredential Resolve(
        string? protectedValue,
        DateTimeOffset? expiresAtUtc)
    {
        string? value;
        try
        {
            value = string.IsNullOrWhiteSpace(protectedValue)
                ? null
                : _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        }

        return new ResolvedCredential
        {
            Value = value,
            ExpiresAtUtc = expiresAtUtc
        };
    }
}
