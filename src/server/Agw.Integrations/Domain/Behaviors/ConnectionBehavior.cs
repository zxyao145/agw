using System.Text.RegularExpressions;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Domain.Behaviors;

public sealed class ConnectionBehavior
{
    private const int MaxAliasLength = 128;
    private const int MaxDisplayNameLength = 200;
    private const string NeedsConfigurationCode = "integration.needs_configuration";
    private const string PendingAuthorizationCode = "integration.pending_authorization";
    private const string CredentialInvalidCode = "integration.credential_invalid";
    private const string CredentialExpiredCode = "integration.credential_expired";
    private const string DefinitionUnavailableCode = "integration.definition_unavailable";
    private static readonly Regex AliasPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    private readonly Connection _connection;

    public ConnectionBehavior(Connection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// <para>别名去除两端空白并转为小写，由小写字母、数字与单个连字符组成，最长 128 个字符。</para>
    /// <para>An alias is trimmed and lower-cased, made of lowercase letters, digits and single hyphens, and at most 128 characters.</para>
    /// </summary>
    public static string NormalizeAlias(string alias)
    {
        var normalized = (alias ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > MaxAliasLength || !AliasPattern.IsMatch(normalized))
        {
            throw new AgwException(ErrorCodes.IntegrationAliasInvalid);
        }

        return normalized;
    }

    public void Create(ResolvedIntegrationDefinition definition, string alias, string displayName, bool enabled)
    {
        _connection.PluginId = definition.Plugin.Id;
        _connection.ConnectorId = definition.Connector.Id;
        _connection.AuthSchemeId = definition.AuthScheme.Id;
        _connection.Alias = NormalizeAlias(alias);
        Configure(displayName, enabled);
    }

    /// <summary>
    /// <para>连接创建后，别名与所用的插件、连接器、认证方案都不能改变。</para>
    /// <para>After creation, a connection's alias and its plugin, connector and auth scheme cannot change.</para>
    /// </summary>
    public void Update(ResolvedIntegrationDefinition definition, string alias, string displayName, bool enabled)
    {
        if (!string.Equals(NormalizeAlias(alias), _connection.Alias, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.ConnectionAliasImmutable);
        }

        if (
            !string.Equals(definition.Plugin.Id, _connection.PluginId, StringComparison.Ordinal)
            || !string.Equals(definition.Connector.Id, _connection.ConnectorId, StringComparison.Ordinal)
            || !string.Equals(definition.AuthScheme.Id, _connection.AuthSchemeId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        Configure(displayName, enabled);
    }

    /// <summary>
    /// <para>写入或清除连接字段对应的密钥槽位；未列出的字段保持原值。</para>
    /// <para>Sets or clears the secret slots of connection fields; unlisted fields keep their values.</para>
    /// </summary>
    public void ApplySecretUpdates(
        IReadOnlyDictionary<string, string> secretsToSet,
        IReadOnlyCollection<string> clearedFieldIds,
        string actor,
        DateTimeOffset now
    )
    {
        foreach (var fieldId in clearedFieldIds)
        {
            RemoveCredential(IntegrationCredentialSlots.ConnectionField(fieldId));
        }

        foreach (var (fieldId, value) in secretsToSet)
        {
            var slot = IntegrationCredentialSlots.ConnectionField(fieldId);
            var credential = FindCredential(slot);
            if (credential == null)
            {
                credential = new ConnectionCredential
                {
                    ConnectionId = _connection.Id,
                    Connection = _connection,
                    Slot = slot,
                    CreateBy = actor,
                    CreateTime = now,
                };
                _connection.Credentials.Add(credential);
            }
            else
            {
                credential.UpdateBy = actor;
                credential.UpdateTime = now;
            }

            credential.Value = value;
            credential.FormatVersion = 1;
        }
    }

    /// <summary>
    /// <para>连接的配置发生变化后，进行中的 OAuth 授权失效。</para>
    /// <para>Changing a connection invalidates any OAuth authorization in progress.</para>
    /// </summary>
    public void CancelPendingAuthorization() => RemoveCredential(IntegrationCredentialSlots.OAuthAuthorizationAttempt);

    public bool HasRequiredConfiguration(
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyDictionary<string, string?> configuration
    ) =>
        fields
            .Where(field => field.IsRequired)
            .All(field =>
                field.Type == FormFieldType.Secret
                    ? FindCredential(IntegrationCredentialSlots.ConnectionField(field.Id)) != null
                    : configuration.TryGetValue(field.Id, out var value) && !string.IsNullOrWhiteSpace(value)
            );

    /// <summary>
    /// <para>返回 OAuth 访问令牌决定的状态：没有令牌时等待授权，令牌过期时为已过期，令牌可用时返回 null。</para>
    /// <para>Returns the status decided by the OAuth access token: pending authorization without a token, expired once it expires, or null when it is usable.</para>
    /// </summary>
    public ConnectionStatus? GetAccessTokenStatus(DateTimeOffset now)
    {
        var accessToken = FindCredential(IntegrationCredentialSlots.OAuthAccessToken);
        if (accessToken == null)
        {
            return ConnectionStatus.PendingAuthorization;
        }

        return accessToken.ExpiresAtUtc <= now ? ConnectionStatus.Expired : null;
    }

    /// <summary>
    /// <para>按配置情况设置状态：停用的连接为已停用，所需的插件安装未配置完成时需要配置，OAuth 连接没有访问令牌时等待授权，其余为未验证。</para>
    /// <para>Sets the status from configuration: disabled when turned off, needs configuration while the required plugin setup is incomplete, pending authorization for an OAuth connection without an access token, otherwise unverified.</para>
    /// </summary>
    public void ApplyConfigurationStatus(bool installationConfigured, AuthSchemeType authSchemeType)
    {
        if (!_connection.Enabled)
        {
            ChangeStatus(ConnectionStatus.Disabled);
        }
        else if (!installationConfigured)
        {
            ChangeStatus(ConnectionStatus.NeedsConfiguration);
        }
        else if (
            authSchemeType == AuthSchemeType.OAuth2
            && FindCredential(IntegrationCredentialSlots.OAuthAccessToken) == null
        )
        {
            ChangeStatus(ConnectionStatus.PendingAuthorization);
        }
        else
        {
            ChangeStatus(ConnectionStatus.Unverified);
        }
    }

    /// <summary>
    /// <para>插件安装变化后，连接的进行中授权与上次验证结果都失效，并按新的安装情况重新设置状态。</para>
    /// <para>After its plugin setup changes, a connection loses its authorization in progress and last validation result, and its status follows the new setup.</para>
    /// </summary>
    public void ResetAfterInstallationChange(bool installationConfigured, AuthSchemeType authSchemeType)
    {
        CancelPendingAuthorization();
        _connection.LastValidatedAtUtc = null;
        _connection.ValidationMetadataJson = null;
        ApplyConfigurationStatus(installationConfigured, authSchemeType);
    }

    /// <summary>
    /// <para>读取时的有效状态：停用的连接显示为已停用，定义不可用的连接显示为定义不可用。</para>
    /// <para>The effective status on read: a disabled connection shows as disabled, and one whose definition is unavailable shows as definition unavailable.</para>
    /// </summary>
    public ConnectionStatus GetEffectiveStatus(bool definitionAvailable)
    {
        if (!_connection.Enabled)
        {
            return ConnectionStatus.Disabled;
        }

        return definitionAvailable ? _connection.Status : ConnectionStatus.DefinitionUnavailable;
    }

    public void RecordValidation(ConnectionStatus status, DateTimeOffset validatedAt)
    {
        _connection.LastValidatedAtUtc = validatedAt;
        ChangeStatus(status);
    }

    public void BeginAuthorization()
    {
        ChangeStatus(ConnectionStatus.PendingAuthorization);
        _connection.LastValidatedAtUtc = null;
    }

    public void MarkAuthorized(DateTimeOffset authorizedAt)
    {
        ChangeStatus(ConnectionStatus.Ready);
        _connection.LastValidatedAtUtc = authorizedAt;
    }

    public void MarkAuthorizationFailed(ConnectionStatus status, string errorCode)
    {
        _connection.Status = status;
        _connection.LastValidatedAtUtc = null;
        _connection.LastValidationErrorCode = errorCode;
    }

    /// <summary>
    /// <para>刷新令牌时，提供方没有返回主体标识则保留原来的主体。</para>
    /// <para>On token refresh, the existing subject stays when the provider returns none.</para>
    /// </summary>
    public void RefreshSubject(string? subject)
    {
        if (!string.IsNullOrWhiteSpace(subject))
        {
            _connection.Subject = subject;
        }
    }

    private void Configure(string displayName, bool enabled)
    {
        var normalizedDisplayName = (displayName ?? string.Empty).Trim();
        if (normalizedDisplayName.Length == 0 || normalizedDisplayName.Length > MaxDisplayNameLength)
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        _connection.DisplayName = normalizedDisplayName;
        _connection.Enabled = enabled;
    }

    private void ChangeStatus(ConnectionStatus status)
    {
        _connection.Status = status;
        _connection.LastValidationErrorCode = status switch
        {
            ConnectionStatus.NeedsConfiguration => NeedsConfigurationCode,
            ConnectionStatus.PendingAuthorization => PendingAuthorizationCode,
            ConnectionStatus.Invalid => CredentialInvalidCode,
            ConnectionStatus.Expired => CredentialExpiredCode,
            ConnectionStatus.DefinitionUnavailable => DefinitionUnavailableCode,
            _ => null,
        };
    }

    private ConnectionCredential? FindCredential(string slot) =>
        _connection.Credentials.FirstOrDefault(credential =>
            string.Equals(credential.Slot, slot, StringComparison.OrdinalIgnoreCase)
        );

    private void RemoveCredential(string slot)
    {
        var credential = FindCredential(slot);
        if (credential != null)
        {
            _connection.Credentials.Remove(credential);
        }
    }
}
