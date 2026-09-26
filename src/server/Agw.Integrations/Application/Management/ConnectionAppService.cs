using Agw.Auth.Contracts;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Behaviors;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Domain.Services;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

public sealed class ConnectionAppService
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly ConnectionAliasUniquenessDomainService _aliasUniqueness;
    private readonly PluginInstallationReadinessDomainService _installationReadiness;
    private readonly IConnectionCredentialReader _credentialReader;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;
    private readonly IntegrationMutationCoordinator _mutations;

    public ConnectionAppService(
        IIntegrationsDbContext dbContext,
        IPluginCatalog pluginCatalog,
        ConnectionAliasUniquenessDomainService aliasUniqueness,
        PluginInstallationReadinessDomainService installationReadiness,
        IConnectionCredentialReader credentialReader,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IntegrationMutationCoordinator mutations
    )
    {
        _dbContext = dbContext;
        _pluginCatalog = pluginCatalog;
        _aliasUniqueness = aliasUniqueness;
        _installationReadiness = installationReadiness;
        _credentialReader = credentialReader;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _mutations = mutations;
    }

    public async Task<IReadOnlyList<ConnectionResponse>> ListAsync(Guid? id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        IQueryable<Connection> query = _dbContext
            .Connections.AsNoTracking()
            .Include(connection => connection.Credentials)
            .Where(connection => connection.CreateBy == user);
        if (id.HasValue)
        {
            query = query.Where(connection => connection.Id == id.Value);
        }

        var connections = await query.OrderBy(connection => connection.Alias).ToListAsync(cancellationToken);
        return connections.Select(Map).ToList();
    }

    public async Task<ConnectionResponse> CreateAsync(
        ConnectionCreateRequest request,
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
        await using var mutation = await _mutations.AcquirePluginAsync(definition.Plugin.Id, cancellationToken);
        cancellationToken = mutation.Token;
        var alias = ConnectionBehavior.NormalizeAlias(request.Alias);
        await _aliasUniqueness.EnsureAliasAvailableAsync(alias, cancellationToken);

        var input = ValidateInput(definition, request.Configuration, request.Secrets, []);
        var now = _timeProvider.GetUtcNow();
        var connection = new Connection
        {
            Id = Guid.CreateVersion7(),
            ConfigurationJson = IntegrationConfigurationCodec.Write(input.Configuration),
            CreateBy = user,
            CreateTime = now,
        };
        var behavior = new ConnectionBehavior(connection);
        behavior.Create(definition, alias, request.DisplayName, request.Enabled);
        await _dbContext.Connections.AddAsync(connection, cancellationToken);
        behavior.ApplySecretUpdates(input.SecretsToSet, input.ClearedSecretFieldIds, user, now);
        var (installationConfigured, _) = await _installationReadiness.ResolveInstallationAsync(
            definition,
            cancellationToken
        );
        behavior.ApplyConfigurationStatus(installationConfigured, definition.AuthScheme.Type);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(connection);
    }

    public async Task<ConnectionResponse> UpdateAsync(
        ConnectionUpdateRequest request,
        CancellationToken cancellationToken
    )
    {
        var user = _userInfoService.RequiredUserId;
        await using var mutation = await _mutations.AcquireConnectionAsync(request.Id, cancellationToken);
        cancellationToken = mutation.Token;
        var connection = await GetTrackedAsync(request.Id, cancellationToken);
        var definition = IntegrationDefinitionResolver.Resolve(
            _pluginCatalog,
            request.PluginId,
            request.ConnectorId,
            request.AuthSchemeId
        );
        var behavior = new ConnectionBehavior(connection);
        behavior.Update(definition, request.Alias, request.DisplayName, request.Enabled);

        var input = ValidateInput(
            definition,
            request.Configuration,
            request.Secrets,
            connection.Credentials.Select(credential => credential.Slot).ToList()
        );
        var now = _timeProvider.GetUtcNow();
        connection.ConfigurationJson = IntegrationConfigurationCodec.Write(input.Configuration);
        connection.UpdateBy = user;
        connection.UpdateTime = now;
        behavior.CancelPendingAuthorization();
        behavior.ApplySecretUpdates(input.SecretsToSet, input.ClearedSecretFieldIds, user, now);
        var (installationConfigured, _) = await _installationReadiness.ResolveInstallationAsync(
            definition,
            cancellationToken
        );
        behavior.ApplyConfigurationStatus(installationConfigured, definition.AuthScheme.Type);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(connection);
    }

    public async Task<ConnectionResponse> ValidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        await using var mutation = await _mutations.AcquireConnectionAsync(id, cancellationToken);
        cancellationToken = mutation.Token;
        var connection = await GetTrackedAsync(id, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        connection.UpdateBy = user;
        connection.UpdateTime = now;
        var status = await CheckStatusAsync(connection, now, cancellationToken);
        new ConnectionBehavior(connection).RecordValidation(status, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(connection);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        var pluginId = await _dbContext
            .Connections.AsNoTracking()
            .Where(item => item.Id == id && item.CreateBy == user)
            .Select(item => item.PluginId)
            .SingleOrDefaultAsync(cancellationToken);
        if (pluginId == null)
            return false;
        await using var mutation = await _mutations.AcquirePluginAsync(pluginId, cancellationToken);
        cancellationToken = mutation.Token;
        var connection = await _dbContext.Connections.FirstOrDefaultAsync(
            item => item.Id == id && item.CreateBy == user,
            cancellationToken
        );
        if (connection == null)
        {
            return false;
        }

        _dbContext.Connections.Remove(connection);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ValidatedIntegrationInput ValidateInput(
        ResolvedIntegrationDefinition definition,
        IDictionary<string, string?>? configuration,
        IDictionary<string, SecretFieldUpdateRequest>? secrets,
        IReadOnlyCollection<string> existingCredentialSlots
    ) =>
        IntegrationInputValidator.Validate(
            definition.AuthScheme.ConnectionFields,
            new Dictionary<string, string?>(
                configuration ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase
            ),
            new Dictionary<string, SecretFieldUpdateRequest>(
                secrets ?? new Dictionary<string, SecretFieldUpdateRequest>(),
                StringComparer.OrdinalIgnoreCase
            ),
            existingCredentialSlots,
            IntegrationCredentialSlots.ConnectionField
        );

    /// <summary>
    /// <para>检查连接当前能否使用：所需配置齐全，并且已保存的凭据都能解密读取；任何读取失败都判定为凭据无效。</para>
    /// <para>Checks whether the connection is usable now: the required configuration is present and every stored credential can be decrypted; any read failure means the credentials are invalid.</para>
    /// </summary>
    private async Task<ConnectionStatus> CheckStatusAsync(
        Connection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (!connection.Enabled)
        {
            return ConnectionStatus.Disabled;
        }

        if (
            !IntegrationDefinitionResolver.TryResolve(
                _pluginCatalog,
                connection.PluginId,
                connection.ConnectorId,
                connection.AuthSchemeId,
                out var definition
            )
        )
        {
            return ConnectionStatus.DefinitionUnavailable;
        }

        try
        {
            var (installationConfigured, installation) = await _installationReadiness.ResolveInstallationAsync(
                definition!,
                cancellationToken
            );
            if (!installationConfigured)
            {
                return ConnectionStatus.NeedsConfiguration;
            }

            if (installation != null)
            {
                await ReadInstallationCredentialsAsync(installation, definition!, cancellationToken);
            }

            var behavior = new ConnectionBehavior(connection);
            var configuration = IntegrationConfigurationCodec.Read(connection.ConfigurationJson);
            if (!behavior.HasRequiredConfiguration(definition!.AuthScheme.ConnectionFields, configuration))
            {
                return ConnectionStatus.NeedsConfiguration;
            }

            var existingSlots = connection.Credentials.Select(credential => credential.Slot).ToList();
            ValidateInput(definition, configuration, null, existingSlots);
            foreach (
                var field in definition.AuthScheme.ConnectionFields.Where(field =>
                    field.Type == FormFieldType.Secret
                    && existingSlots.Contains(
                        IntegrationCredentialSlots.ConnectionField(field.Id),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
            )
            {
                await _credentialReader.ReadConnectionAsync(
                    connection.Id,
                    IntegrationCredentialSlots.ConnectionField(field.Id),
                    cancellationToken
                );
            }

            if (definition.AuthScheme.Type == AuthSchemeType.OAuth2)
            {
                var accessTokenStatus = behavior.GetAccessTokenStatus(now);
                if (accessTokenStatus.HasValue)
                {
                    return accessTokenStatus.Value;
                }

                await _credentialReader.ReadConnectionAsync(
                    connection.Id,
                    IntegrationCredentialSlots.OAuthAccessToken,
                    cancellationToken
                );
            }

            return ConnectionStatus.Ready;
        }
        catch (AgwException)
        {
            return ConnectionStatus.Invalid;
        }
    }

    private async Task ReadInstallationCredentialsAsync(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        CancellationToken cancellationToken
    )
    {
        foreach (
            var credential in installation.Credentials.Where(credential =>
                credential.Slot.StartsWith(
                    $"field:{definition.Connector.Id}:{definition.AuthScheme.Id}:",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            await _credentialReader.ReadPluginInstallationAsync(installation.Id, credential.Slot, cancellationToken);
        }
    }

    private async Task<Connection> GetTrackedAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        var connection = await _dbContext
            .Connections.Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.Id == id && item.CreateBy == user, cancellationToken);
        return connection ?? throw new AgwException(ErrorCodes.ConnectionNotFound);
    }

    private ConnectionResponse Map(Connection connection)
    {
        var storedConfiguration = IntegrationConfigurationCodec.Read(connection.ConfigurationJson);
        var hasDefinition = IntegrationDefinitionResolver.TryResolve(
            _pluginCatalog,
            connection.PluginId,
            connection.ConnectorId,
            connection.AuthSchemeId,
            out var definition
        );
        var configuration = hasDefinition
            ? definition!
                .AuthScheme.ConnectionFields.Where(field => field.Type != FormFieldType.Secret)
                .Where(field => storedConfiguration.ContainsKey(field.Id))
                .ToDictionary(
                    field => field.Id,
                    field => storedConfiguration[field.Id],
                    StringComparer.OrdinalIgnoreCase
                )
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var secretFieldIds = hasDefinition
            ? definition!
                .AuthScheme.ConnectionFields.Where(field => field.Type == FormFieldType.Secret)
                .Select(field => field.Id)
            : connection
                .Credentials.Where(credential => credential.Slot.StartsWith("field:", StringComparison.Ordinal))
                .Select(credential => credential.Slot["field:".Length..]);
        var secrets = secretFieldIds.ToDictionary(
            fieldId => fieldId,
            fieldId =>
            {
                var credential = connection.Credentials.FirstOrDefault(item =>
                    string.Equals(
                        item.Slot,
                        IntegrationCredentialSlots.ConnectionField(fieldId),
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                return new SecretFieldStateResponse { Configured = credential != null };
            },
            StringComparer.OrdinalIgnoreCase
        );
        var accessToken = connection.Credentials.FirstOrDefault(credential =>
            string.Equals(
                credential.Slot,
                IntegrationCredentialSlots.OAuthAccessToken,
                StringComparison.OrdinalIgnoreCase
            )
        );

        return new ConnectionResponse
        {
            Id = connection.Id,
            PluginId = connection.PluginId,
            ConnectorId = connection.ConnectorId,
            AuthSchemeId = connection.AuthSchemeId,
            DisplayName = connection.DisplayName,
            Alias = connection.Alias,
            Enabled = connection.Enabled,
            Status = (ConnectionStatusResponse)new ConnectionBehavior(connection).GetEffectiveStatus(hasDefinition),
            Subject = connection.Subject,
            ExpiresAtUtc = accessToken?.ExpiresAtUtc,
            LastValidatedAtUtc = connection.LastValidatedAtUtc,
            LastValidationErrorCode = connection.LastValidationErrorCode,
            Configuration = configuration,
            Secrets = secrets,
        };
    }
}
