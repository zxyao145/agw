using Agw.Auth.Contracts;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Behaviors;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Domain.Services;
using Agw.Shared.Data.Entities.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

public sealed class PluginInstallationAppService
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly PluginInstallationReadinessDomainService _installationReadiness;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;
    private readonly IntegrationMutationCoordinator _mutations;

    public PluginInstallationAppService(
        IIntegrationsDbContext dbContext,
        IPluginCatalog pluginCatalog,
        PluginInstallationReadinessDomainService installationReadiness,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IntegrationMutationCoordinator mutations
    )
    {
        _dbContext = dbContext;
        _pluginCatalog = pluginCatalog;
        _installationReadiness = installationReadiness;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _mutations = mutations;
    }

    public async Task<PluginInstallationResponse> UpsertAsync(
        PluginInstallationUpsertRequest request,
        CancellationToken cancellationToken
    )
    {
        var user = _userInfoService.RequiredUserId;

        var definition = IntegrationDefinitionResolver.Resolve(
            _pluginCatalog,
            request.PluginId,
            request.ConnectorId,
            request.AuthSchemeId
        );
        var pluginId = definition.Plugin.Id;
        await using var mutation = await _mutations.AcquirePluginAsync(pluginId, cancellationToken);
        cancellationToken = mutation.Token;
        var connectorId = definition.Connector.Id;
        var authSchemeId = definition.AuthScheme.Id;
        var now = _timeProvider.GetUtcNow();
        var installation = await _dbContext
            .PluginInstallations.Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.PluginId == pluginId && item.CreateBy == user, cancellationToken);
        if (installation == null)
        {
            installation = new PluginInstallation
            {
                Id = Guid.CreateVersion7(),
                PluginId = pluginId,
                Enabled = request.Enabled,
                ConfigurationJson = "{}",
                CreateBy = user,
                CreateTime = now,
            };
            await _dbContext.PluginInstallations.AddAsync(installation, cancellationToken);
        }
        else
        {
            installation.Enabled = request.Enabled;
            installation.UpdateBy = user;
            installation.UpdateTime = now;
        }

        var slotFactory = (string fieldId) =>
            IntegrationCredentialSlots.InstallationField(connectorId, authSchemeId, fieldId);
        var input = IntegrationInputValidator.Validate(
            definition.AuthScheme.InstallationFields,
            new Dictionary<string, string?>(
                request.Configuration ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase
            ),
            new Dictionary<string, SecretFieldUpdateRequest>(
                request.Secrets ?? new Dictionary<string, SecretFieldUpdateRequest>(),
                StringComparer.OrdinalIgnoreCase
            ),
            installation.Credentials.Select(credential => credential.Slot).ToList(),
            slotFactory,
            allowClearingExistingRequiredSecrets: true,
            allowMissingRequiredFields: !request.Enabled
        );

        var allConfiguration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        IntegrationConfigurationCodec.ReplaceInstallationScope(
            allConfiguration,
            connectorId,
            authSchemeId,
            definition
                .AuthScheme.InstallationFields.Where(field => field.Type != FormFieldType.Secret)
                .Select(field => field.Id)
                .ToList(),
            input.Configuration
        );
        installation.ConfigurationJson = IntegrationConfigurationCodec.Write(allConfiguration);

        new PluginInstallationBehavior(installation).ApplySecretUpdates(
            definition,
            input.SecretsToSet,
            input.ClearedSecretFieldIds,
            user,
            now
        );
        var affectedConnections = await _installationReadiness.ResetAffectedConnectionsAsync(
            installation,
            definition,
            input.Configuration,
            cancellationToken
        );
        foreach (var connection in affectedConnections)
        {
            connection.UpdateBy = user;
            connection.UpdateTime = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Map(installation, definition, input.Configuration);
    }

    private static PluginInstallationResponse Map(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        IReadOnlyDictionary<string, string?> configuration
    )
    {
        var secrets = definition
            .AuthScheme.InstallationFields.Where(field => field.Type == FormFieldType.Secret)
            .ToDictionary(
                field => field.Id,
                field =>
                    MapSecret(
                        installation.Credentials.FirstOrDefault(credential =>
                            string.Equals(
                                credential.Slot,
                                IntegrationCredentialSlots.InstallationField(
                                    definition.Connector.Id,
                                    definition.AuthScheme.Id,
                                    field.Id
                                ),
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    ),
                StringComparer.OrdinalIgnoreCase
            );

        return new PluginInstallationResponse
        {
            Id = installation.Id,
            PluginId = definition.Plugin.Id,
            ConnectorId = definition.Connector.Id,
            AuthSchemeId = definition.AuthScheme.Id,
            Enabled = installation.Enabled,
            Configuration = configuration,
            Secrets = secrets,
        };
    }

    private static SecretFieldStateResponse MapSecret(PluginInstallationCredential? credential) =>
        new() { Configured = credential != null };
}
