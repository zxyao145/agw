using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

public sealed class PluginInstallationAppService
{
    private const string NeedsConfigurationCode = "integration.needs_configuration";
    private const string PendingAuthorizationCode = "integration.pending_authorization";

    private readonly IRepository<PluginInstallation> _installationRepository;
    private readonly IRepository<Connection> _connectionRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly CredentialMutationService _credentialMutations;
    private readonly TimeProvider _timeProvider;

    public PluginInstallationAppService(
        IRepository<PluginInstallation> installationRepository,
        IRepository<Connection> connectionRepository,
        IUnitOfWork unitOfWork,
        IPluginCatalog pluginCatalog,
        CredentialMutationService credentialMutations,
        TimeProvider timeProvider)
    {
        _installationRepository = installationRepository;
        _connectionRepository = connectionRepository;
        _unitOfWork = unitOfWork;
        _pluginCatalog = pluginCatalog;
        _credentialMutations = credentialMutations;
        _timeProvider = timeProvider;
    }

    public async Task<PluginInstallationResponse> UpsertAsync(
        PluginInstallationUpsertRequest request,
        string user,
        CancellationToken cancellationToken)
    {
        var definition = IntegrationDefinitionResolver.Resolve(
            _pluginCatalog,
            request.PluginId,
            request.ConnectorId,
            request.AuthSchemeId);
        var pluginId = definition.Plugin.Id;
        var connectorId = definition.Connector.Id;
        var authSchemeId = definition.AuthScheme.Id;
        var installation = await _installationRepository.Queryable
            .Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.PluginId == pluginId, cancellationToken);
        if (installation == null)
        {
            installation = new PluginInstallation
            {
                Id = Guid.NewGuid(),
                PluginId = pluginId,
                Enabled = request.Enabled,
                ConfigurationJson = "{}",
                CreateBy = user,
                CreateTime = _timeProvider.GetUtcNow()
            };
            await _installationRepository.AddAsync(installation);
        }
        else
        {
            installation.Enabled = request.Enabled;
            installation.UpdateBy = user;
            installation.UpdateTime = _timeProvider.GetUtcNow();
        }

        var slotFactory = (string fieldId) =>
            IntegrationCredentialSlots.InstallationField(connectorId, authSchemeId, fieldId);
        var input = IntegrationInputValidator.Validate(
            definition.AuthScheme.InstallationFields,
            new Dictionary<string, string?>(
                request.Configuration ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SecretFieldUpdateRequest>(
                request.Secrets ?? new Dictionary<string, SecretFieldUpdateRequest>(),
                StringComparer.OrdinalIgnoreCase),
            installation.Credentials.Select(credential => credential.Slot).ToList(),
            slotFactory,
            allowClearingExistingRequiredSecrets: true,
            allowMissingRequiredFields: !request.Enabled);

        var allConfiguration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        IntegrationConfigurationCodec.ReplaceInstallationScope(
            allConfiguration,
            connectorId,
            authSchemeId,
            definition.AuthScheme.InstallationFields
                .Where(field => field.Type != FormFieldType.Secret)
                .Select(field => field.Id)
                .ToList(),
            input.Configuration);
        installation.ConfigurationJson = IntegrationConfigurationCodec.Write(allConfiguration);

        await _credentialMutations.ApplyInstallationAsync(
            installation,
            input.SecretUpdates,
            connectorId,
            authSchemeId,
            user);
        await InvalidateConnectionsAsync(installation, definition, user, cancellationToken);
        await _unitOfWork.SaveChangesAsync();

        return Map(installation, definition, input.Configuration);
    }

    private async Task InvalidateConnectionsAsync(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        string user,
        CancellationToken cancellationToken)
    {
        var query = _connectionRepository.Queryable
            .Include(connection => connection.Credentials)
            .Where(connection => connection.PluginId == installation.PluginId);
        if (installation.Enabled)
        {
            query = query.Where(connection =>
                connection.ConnectorId == definition.Connector.Id
                && connection.AuthSchemeId == definition.AuthScheme.Id);
        }

        var connections = await query.ToListAsync(cancellationToken);
        var scopeConfigured = installation.Enabled
            && HasRequiredConfiguration(installation, definition);
        var now = _timeProvider.GetUtcNow();
        foreach (var connection in connections)
        {
            connection.LastValidatedAtUtc = null;
            connection.ValidationMetadataJson = null;
            connection.UpdateBy = user;
            connection.UpdateTime = now;

            if (!connection.Enabled)
            {
                SetStatus(connection, ConnectionStatus.Disabled, null);
            }
            else if (!installation.Enabled || !scopeConfigured)
            {
                SetStatus(connection, ConnectionStatus.NeedsConfiguration, NeedsConfigurationCode);
            }
            else if (definition.AuthScheme.Type == AuthSchemeType.OAuth2
                && !connection.Credentials.Any(credential => string.Equals(
                    credential.Slot,
                    IntegrationCredentialSlots.OAuthAccessToken,
                    StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus(connection, ConnectionStatus.PendingAuthorization, PendingAuthorizationCode);
            }
            else
            {
                SetStatus(connection, ConnectionStatus.Unverified, null);
            }
        }
    }

    private static bool HasRequiredConfiguration(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition)
    {
        var configuration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        foreach (var field in definition.AuthScheme.InstallationFields.Where(field => field.IsRequired))
        {
            if (field.Type == FormFieldType.Secret)
            {
                var slot = IntegrationCredentialSlots.InstallationField(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    field.Id);
                if (!installation.Credentials.Any(credential => string.Equals(
                    credential.Slot,
                    slot,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            else
            {
                var key = IntegrationConfigurationCodec.InstallationKey(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    field.Id);
                if (!configuration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void SetStatus(Connection connection, ConnectionStatus status, string? errorCode)
    {
        connection.Status = status;
        connection.LastValidationErrorCode = errorCode;
    }

    private static PluginInstallationResponse Map(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        IReadOnlyDictionary<string, string?> configuration)
    {
        var secrets = definition.AuthScheme.InstallationFields
            .Where(field => field.Type == FormFieldType.Secret)
            .ToDictionary(
                field => field.Id,
                field => MapSecret(installation.Credentials.FirstOrDefault(credential =>
                    string.Equals(
                        credential.Slot,
                        IntegrationCredentialSlots.InstallationField(
                            definition.Connector.Id,
                            definition.AuthScheme.Id,
                            field.Id),
                        StringComparison.OrdinalIgnoreCase))),
                StringComparer.OrdinalIgnoreCase);

        return new PluginInstallationResponse
        {
            Id = installation.Id,
            PluginId = definition.Plugin.Id,
            ConnectorId = definition.Connector.Id,
            AuthSchemeId = definition.AuthScheme.Id,
            Enabled = installation.Enabled,
            Configuration = configuration,
            Secrets = secrets
        };
    }

    private static SecretFieldStateResponse MapSecret(PluginInstallationCredential? credential) => new()
    {
        Configured = credential != null
    };
}
