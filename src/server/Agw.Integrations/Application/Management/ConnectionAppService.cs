using Agw.Auth.Application;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

public sealed class ConnectionAppService
{
    private const string NeedsConfigurationCode = "integration.needs_configuration";
    private const string PendingAuthorizationCode = "integration.pending_authorization";
    private const string CredentialInvalidCode = "integration.credential_invalid";
    private const string CredentialExpiredCode = "integration.credential_expired";
    private const string DefinitionUnavailableCode = "integration.definition_unavailable";

    private readonly IRepository<Connection> _connectionRepository;
    private readonly IRepository<PluginInstallation> _installationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly CredentialMutationService _credentialMutations;
    private readonly IConnectionCredentialReader _credentialReader;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public ConnectionAppService(
        IRepository<Connection> connectionRepository,
        IRepository<PluginInstallation> installationRepository,
        IUnitOfWork unitOfWork,
        IPluginCatalog pluginCatalog,
        CredentialMutationService credentialMutations,
        IConnectionCredentialReader credentialReader,
        TimeProvider timeProvider,
        IUserInfoService userInfoService
    )
    {
        _connectionRepository = connectionRepository;
        _installationRepository = installationRepository;
        _unitOfWork = unitOfWork;
        _pluginCatalog = pluginCatalog;
        _credentialMutations = credentialMutations;
        _credentialReader = credentialReader;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<ConnectionResponse>> ListAsync(Guid? id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        IQueryable<Connection> query = _connectionRepository
            .Queryable.Include(connection => connection.Credentials)
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
        var alias = IntegrationInputValidator.NormalizeAlias(request.Alias);
        if (
            await _connectionRepository.Queryable.AnyAsync(
                connection => connection.CreateBy == user && connection.Alias == alias,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ConnectionAliasAlreadyExists);
        }

        var input = IntegrationInputValidator.Validate(
            definition.AuthScheme.ConnectionFields,
            new Dictionary<string, string?>(
                request.Configuration ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase
            ),
            new Dictionary<string, SecretFieldUpdateRequest>(
                request.Secrets ?? new Dictionary<string, SecretFieldUpdateRequest>(),
                StringComparer.OrdinalIgnoreCase
            ),
            [],
            IntegrationCredentialSlots.ConnectionField
        );
        var connection = new Connection
        {
            Id = Guid.CreateVersion7(),
            PluginId = definition.Plugin.Id,
            ConnectorId = definition.Connector.Id,
            AuthSchemeId = definition.AuthScheme.Id,
            DisplayName = IntegrationInputValidator.RequireDisplayName(request.DisplayName),
            Alias = alias,
            ConfigurationJson = IntegrationConfigurationCodec.Write(input.Configuration),
            Enabled = request.Enabled,
            CreateBy = user,
            CreateTime = _timeProvider.GetUtcNow(),
        };
        await _connectionRepository.AddAsync(connection);
        await _credentialMutations.ApplyConnectionAsync(connection, input.SecretUpdates);
        await SetInitialStatusAsync(connection, definition, cancellationToken);
        await _unitOfWork.SaveChangesAsync();
        return Map(connection);
    }

    public async Task<ConnectionResponse> UpdateAsync(
        ConnectionUpdateRequest request,
        CancellationToken cancellationToken
    )
    {
        var user = _userInfoService.RequiredUserId;
        var connection = await GetTrackedAsync(request.Id, cancellationToken);
        var alias = IntegrationInputValidator.NormalizeAlias(request.Alias);
        if (!string.Equals(alias, connection.Alias, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.ConnectionAliasImmutable);
        }

        var definition = IntegrationDefinitionResolver.Resolve(
            _pluginCatalog,
            request.PluginId,
            request.ConnectorId,
            request.AuthSchemeId
        );
        if (
            !string.Equals(definition.Plugin.Id, connection.PluginId, StringComparison.Ordinal)
            || !string.Equals(definition.Connector.Id, connection.ConnectorId, StringComparison.Ordinal)
            || !string.Equals(definition.AuthScheme.Id, connection.AuthSchemeId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        var input = IntegrationInputValidator.Validate(
            definition.AuthScheme.ConnectionFields,
            new Dictionary<string, string?>(
                request.Configuration ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase
            ),
            new Dictionary<string, SecretFieldUpdateRequest>(
                request.Secrets ?? new Dictionary<string, SecretFieldUpdateRequest>(),
                StringComparer.OrdinalIgnoreCase
            ),
            connection.Credentials.Select(credential => credential.Slot).ToList(),
            IntegrationCredentialSlots.ConnectionField
        );
        connection.DisplayName = IntegrationInputValidator.RequireDisplayName(request.DisplayName);
        connection.ConfigurationJson = IntegrationConfigurationCodec.Write(input.Configuration);
        connection.Enabled = request.Enabled;
        connection.UpdateBy = user;
        connection.UpdateTime = _timeProvider.GetUtcNow();
        await _credentialMutations.ApplyConnectionAsync(connection, input.SecretUpdates);
        await SetInitialStatusAsync(connection, definition, cancellationToken);
        await _unitOfWork.SaveChangesAsync();
        return Map(connection);
    }

    public async Task<ConnectionResponse> ValidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        var connection = await GetTrackedAsync(id, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        connection.LastValidatedAtUtc = now;
        connection.UpdateBy = user;
        connection.UpdateTime = now;

        if (!connection.Enabled)
        {
            SetStatus(connection, ConnectionStatus.Disabled, null);
        }
        else if (
            !IntegrationDefinitionResolver.TryResolve(
                _pluginCatalog,
                connection.PluginId,
                connection.ConnectorId,
                connection.AuthSchemeId,
                out var definition
            )
        )
        {
            SetStatus(connection, ConnectionStatus.DefinitionUnavailable, DefinitionUnavailableCode);
        }
        else
        {
            try
            {
                var installationStatus = await ResolveInstallationStatusAsync(
                    connection,
                    definition!,
                    validateReadable: true,
                    cancellationToken
                );
                if (installationStatus.HasValue)
                {
                    SetStatus(
                        connection,
                        installationStatus.Value,
                        installationStatus == ConnectionStatus.Invalid ? CredentialInvalidCode : NeedsConfigurationCode
                    );
                }
                else
                {
                    await ValidateResolvedAsync(connection, definition!, now, cancellationToken);
                }
            }
            catch (AgwException)
            {
                SetStatus(connection, ConnectionStatus.Invalid, CredentialInvalidCode);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        return Map(connection);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        var connection = await _connectionRepository.Queryable.FirstOrDefaultAsync(
            item => item.Id == id && item.CreateBy == user,
            cancellationToken
        );
        if (connection == null)
        {
            return false;
        }

        _connectionRepository.Remove(connection);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task ValidateResolvedAsync(
        Connection connection,
        ResolvedIntegrationDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var configuration = IntegrationConfigurationCodec.Read(connection.ConfigurationJson);
        if (
            !HasRequiredConfiguration(
                definition.AuthScheme.ConnectionFields,
                configuration,
                connection.Credentials.ToList()
            )
        )
        {
            SetStatus(connection, ConnectionStatus.NeedsConfiguration, NeedsConfigurationCode);
            return;
        }

        try
        {
            IntegrationInputValidator.Validate(
                definition.AuthScheme.ConnectionFields,
                configuration,
                new Dictionary<string, SecretFieldUpdateRequest>(),
                connection.Credentials.Select(credential => credential.Slot).ToList(),
                IntegrationCredentialSlots.ConnectionField
            );
            foreach (
                var field in definition.AuthScheme.ConnectionFields.Where(field =>
                    field.Type == FormFieldType.Secret
                    && connection.Credentials.Any(credential =>
                        string.Equals(
                            credential.Slot,
                            IntegrationCredentialSlots.ConnectionField(field.Id),
                            StringComparison.OrdinalIgnoreCase
                        )
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
        }
        catch (AgwException)
        {
            SetStatus(connection, ConnectionStatus.Invalid, CredentialInvalidCode);
            return;
        }

        if (definition.AuthScheme.Type == AuthSchemeType.OAuth2)
        {
            var accessToken = connection.Credentials.FirstOrDefault(credential =>
                string.Equals(
                    credential.Slot,
                    IntegrationCredentialSlots.OAuthAccessToken,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (accessToken == null)
            {
                SetStatus(connection, ConnectionStatus.PendingAuthorization, PendingAuthorizationCode);
                return;
            }

            if (accessToken.ExpiresAtUtc.HasValue && accessToken.ExpiresAtUtc.Value <= now)
            {
                SetStatus(connection, ConnectionStatus.Expired, CredentialExpiredCode);
                return;
            }

            try
            {
                await _credentialReader.ReadConnectionAsync(
                    connection.Id,
                    IntegrationCredentialSlots.OAuthAccessToken,
                    cancellationToken
                );
            }
            catch (AgwException)
            {
                SetStatus(connection, ConnectionStatus.Invalid, CredentialInvalidCode);
                return;
            }
        }

        SetStatus(connection, ConnectionStatus.Ready, null);
    }

    private async Task<Connection> GetTrackedAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = _userInfoService.RequiredUserId;
        var connection = await _connectionRepository
            .Queryable.Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.Id == id && item.CreateBy == user, cancellationToken);
        return connection ?? throw new AgwException(ErrorCodes.ConnectionNotFound);
    }

    private static bool HasRequiredConfiguration(
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyDictionary<string, string?> configuration,
        IReadOnlyCollection<ConnectionCredential> credentials
    )
    {
        foreach (var field in fields.Where(field => field.IsRequired))
        {
            if (field.Type == FormFieldType.Secret)
            {
                if (
                    !credentials.Any(credential =>
                        string.Equals(
                            credential.Slot,
                            IntegrationCredentialSlots.ConnectionField(field.Id),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    return false;
                }
            }
            else if (!configuration.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SetInitialStatusAsync(
        Connection connection,
        ResolvedIntegrationDefinition definition,
        CancellationToken cancellationToken
    )
    {
        if (!connection.Enabled)
        {
            SetStatus(connection, ConnectionStatus.Disabled, null);
            return;
        }

        var installationStatus = await ResolveInstallationStatusAsync(
            connection,
            definition,
            validateReadable: false,
            cancellationToken
        );
        if (installationStatus.HasValue)
        {
            SetStatus(connection, installationStatus.Value, NeedsConfigurationCode);
            return;
        }

        if (
            definition.AuthScheme.Type == AuthSchemeType.OAuth2
            && !connection.Credentials.Any(credential =>
                string.Equals(
                    credential.Slot,
                    IntegrationCredentialSlots.OAuthAccessToken,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            SetStatus(connection, ConnectionStatus.PendingAuthorization, PendingAuthorizationCode);
            return;
        }

        SetStatus(connection, ConnectionStatus.Unverified, null);
    }

    private async Task<ConnectionStatus?> ResolveInstallationStatusAsync(
        Connection connection,
        ResolvedIntegrationDefinition definition,
        bool validateReadable,
        CancellationToken cancellationToken
    )
    {
        if (definition.AuthScheme.InstallationFields.Count == 0)
        {
            return null;
        }

        var installation = await _installationRepository
            .Queryable.Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.PluginId == connection.PluginId, cancellationToken);
        if (installation == null || !installation.Enabled)
        {
            return ConnectionStatus.NeedsConfiguration;
        }

        var allConfiguration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        foreach (var field in definition.AuthScheme.InstallationFields.Where(field => field.IsRequired))
        {
            if (field.Type == FormFieldType.Secret)
            {
                var slot = IntegrationCredentialSlots.InstallationField(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    field.Id
                );
                if (
                    !installation.Credentials.Any(credential =>
                        string.Equals(credential.Slot, slot, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    return ConnectionStatus.NeedsConfiguration;
                }
            }
            else
            {
                var key = IntegrationConfigurationCodec.InstallationKey(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    field.Id
                );
                if (!allConfiguration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return ConnectionStatus.NeedsConfiguration;
                }
            }
        }

        if (!validateReadable)
        {
            return null;
        }

        try
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
                await _credentialReader.ReadPluginInstallationAsync(
                    installation.Id,
                    credential.Slot,
                    cancellationToken
                );
            }
        }
        catch (AgwException)
        {
            return ConnectionStatus.Invalid;
        }

        return null;
    }

    private static void SetStatus(Connection connection, ConnectionStatus status, string? errorCode)
    {
        connection.Status = status;
        connection.LastValidationErrorCode = errorCode;
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
            Status =
                !connection.Enabled ? ConnectionStatusResponse.Disabled
                : hasDefinition ? (ConnectionStatusResponse)connection.Status
                : ConnectionStatusResponse.DefinitionUnavailable,
            Subject = connection.Subject,
            ExpiresAtUtc = accessToken?.ExpiresAtUtc,
            LastValidatedAtUtc = connection.LastValidatedAtUtc,
            LastValidationErrorCode = connection.LastValidationErrorCode,
            Configuration = configuration,
            Secrets = secrets,
        };
    }
}
