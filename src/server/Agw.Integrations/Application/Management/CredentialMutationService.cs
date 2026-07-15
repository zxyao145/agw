using Agw.Integrations.Contracts.Management;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Application.Management;

public sealed class CredentialMutationService
{
    private readonly IRepository<PluginInstallationCredential> _installationCredentialRepository;
    private readonly IRepository<ConnectionCredential> _connectionCredentialRepository;
    private readonly TimeProvider _timeProvider;

    public CredentialMutationService(
        IRepository<PluginInstallationCredential> installationCredentialRepository,
        IRepository<ConnectionCredential> connectionCredentialRepository,
        TimeProvider timeProvider)
    {
        _installationCredentialRepository = installationCredentialRepository;
        _connectionCredentialRepository = connectionCredentialRepository;
        _timeProvider = timeProvider;
    }

    public async Task ApplyInstallationAsync(
        PluginInstallation installation,
        IReadOnlyDictionary<string, SecretFieldUpdateRequest> updates,
        string connectorId,
        string authSchemeId,
        string user)
    {
        foreach (var item in updates)
        {
            var slot = IntegrationCredentialSlots.InstallationField(connectorId, authSchemeId, item.Key);
            var existing = installation.Credentials.FirstOrDefault(credential =>
                string.Equals(credential.Slot, slot, StringComparison.OrdinalIgnoreCase));
            switch (item.Value.Action)
            {
                case SecretUpdateAction.Keep:
                    break;
                case SecretUpdateAction.Clear:
                    if (existing != null)
                    {
                        _installationCredentialRepository.Remove(existing);
                        installation.Credentials.Remove(existing);
                    }
                    break;
                case SecretUpdateAction.Set:
                    if (existing == null)
                    {
                        existing = new PluginInstallationCredential
                        {
                            Id = Guid.NewGuid(),
                            PluginInstallationId = installation.Id,
                            PluginInstallation = installation,
                            Slot = slot,
                            CreateBy = user,
                            CreateTime = _timeProvider.GetUtcNow()
                        };
                        await _installationCredentialRepository.AddAsync(existing);
                    }
                    else
                    {
                        existing.UpdateBy = user;
                        existing.UpdateTime = _timeProvider.GetUtcNow();
                    }
                    ApplySet(existing, item.Value);
                    break;
                default:
                    throw new AgwException(ErrorCodes.IntegrationSecretMutationInvalid);
            }
        }
    }

    public async Task ApplyConnectionAsync(
        Connection connection,
        IReadOnlyDictionary<string, SecretFieldUpdateRequest> updates,
        string user)
    {
        foreach (var item in updates)
        {
            var slot = IntegrationCredentialSlots.ConnectionField(item.Key);
            var existing = connection.Credentials.FirstOrDefault(credential =>
                string.Equals(credential.Slot, slot, StringComparison.OrdinalIgnoreCase));
            switch (item.Value.Action)
            {
                case SecretUpdateAction.Keep:
                    break;
                case SecretUpdateAction.Clear:
                    if (existing != null)
                    {
                        _connectionCredentialRepository.Remove(existing);
                        connection.Credentials.Remove(existing);
                    }
                    break;
                case SecretUpdateAction.Set:
                    if (existing == null)
                    {
                        existing = new ConnectionCredential
                        {
                            Id = Guid.NewGuid(),
                            ConnectionId = connection.Id,
                            Connection = connection,
                            Slot = slot,
                            CreateBy = user,
                            CreateTime = _timeProvider.GetUtcNow()
                        };
                        await _connectionCredentialRepository.AddAsync(existing);
                    }
                    else
                    {
                        existing.UpdateBy = user;
                        existing.UpdateTime = _timeProvider.GetUtcNow();
                    }
                    ApplySet(existing, item.Value);
                    break;
                default:
                    throw new AgwException(ErrorCodes.IntegrationSecretMutationInvalid);
            }
        }
    }

    private static void ApplySet(PluginInstallationCredential credential, SecretFieldUpdateRequest update)
    {
        credential.Value = update.SecretValue!;
        credential.FormatVersion = 1;
    }

    private static void ApplySet(ConnectionCredential credential, SecretFieldUpdateRequest update)
    {
        credential.Value = update.SecretValue!;
        credential.FormatVersion = 1;
    }
}
