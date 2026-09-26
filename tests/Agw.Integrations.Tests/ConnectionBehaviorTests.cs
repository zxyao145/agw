using Agw.Integrations.Domain.Behaviors;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Tests;

public class ConnectionBehaviorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalizeAlias_MixedCaseWithWhitespace_ReturnsTrimmedLowercase()
    {
        Assert.Equal("github-work", ConnectionBehavior.NormalizeAlias("  GitHub-Work "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-github")]
    [InlineData("github-")]
    [InlineData("git--hub")]
    [InlineData("git_hub")]
    public void NormalizeAlias_InvalidAlias_ThrowsIntegrationAliasInvalid(string alias)
    {
        var exception = Assert.Throws<AgwException>(() => ConnectionBehavior.NormalizeAlias(alias));

        Assert.Equal(ErrorCodes.IntegrationAliasInvalid.Code, exception.Code);
    }

    [Fact]
    public void NormalizeAlias_LongerThan128Characters_ThrowsIntegrationAliasInvalid()
    {
        var exception = Assert.Throws<AgwException>(() => ConnectionBehavior.NormalizeAlias(new string('a', 129)));

        Assert.Equal(ErrorCodes.IntegrationAliasInvalid.Code, exception.Code);
    }

    [Fact]
    public void Create_ValidInput_AssignsDefinitionAliasAndTrimmedDisplayName()
    {
        var connection = new Connection();

        new ConnectionBehavior(connection).Create(CreateDefinition(AuthSchemeType.ApiKey), "Work", " Work ", false);

        Assert.Equal("plugin", connection.PluginId);
        Assert.Equal("connector", connection.ConnectorId);
        Assert.Equal("scheme", connection.AuthSchemeId);
        Assert.Equal("work", connection.Alias);
        Assert.Equal("Work", connection.DisplayName);
        Assert.False(connection.Enabled);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_BlankDisplayName_ThrowsIntegrationConfigurationInvalid(string? displayName)
    {
        var exception = Assert.Throws<AgwException>(() =>
            new ConnectionBehavior(new Connection()).Create(
                CreateDefinition(AuthSchemeType.ApiKey),
                "work",
                displayName!,
                true
            )
        );

        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, exception.Code);
    }

    [Fact]
    public void Create_DisplayNameLongerThan200Characters_ThrowsIntegrationConfigurationInvalid()
    {
        var exception = Assert.Throws<AgwException>(() =>
            new ConnectionBehavior(new Connection()).Create(
                CreateDefinition(AuthSchemeType.ApiKey),
                "work",
                new string('a', 201),
                true
            )
        );

        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, exception.Code);
    }

    [Fact]
    public void Update_DifferentAlias_ThrowsConnectionAliasImmutable()
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);

        var exception = Assert.Throws<AgwException>(() =>
            new ConnectionBehavior(connection).Update(CreateDefinition(AuthSchemeType.ApiKey), "other", "Work", true)
        );

        Assert.Equal(ErrorCodes.ConnectionAliasImmutable.Code, exception.Code);
    }

    [Fact]
    public void Update_DifferentAuthScheme_ThrowsIntegrationConfigurationInvalid()
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);

        var exception = Assert.Throws<AgwException>(() =>
            new ConnectionBehavior(connection).Update(
                CreateDefinition(AuthSchemeType.ApiKey, authSchemeId: "other"),
                "work",
                "Work",
                true
            )
        );

        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, exception.Code);
    }

    [Fact]
    public void Update_SameIdentity_UpdatesDisplayNameAndEnabled()
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);

        new ConnectionBehavior(connection).Update(CreateDefinition(AuthSchemeType.ApiKey), " WORK ", "Renamed", false);

        Assert.Equal("Renamed", connection.DisplayName);
        Assert.False(connection.Enabled);
    }

    [Fact]
    public void ApplySecretUpdates_SetAndClear_UpdatesOnlyListedSlots()
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);
        var existing = AddCredential(connection, IntegrationCredentialSlots.ConnectionField("token"), "old");
        AddCredential(connection, IntegrationCredentialSlots.ConnectionField("cleared"), "value");
        var kept = AddCredential(connection, IntegrationCredentialSlots.ConnectionField("kept"), "value");

        new ConnectionBehavior(connection).ApplySecretUpdates(
            new Dictionary<string, string> { ["token"] = "new", ["added"] = "created" },
            ["cleared"],
            "actor",
            UtcNow
        );

        Assert.Equal(3, connection.Credentials.Count);
        Assert.Same(existing, FindCredential(connection, IntegrationCredentialSlots.ConnectionField("token")));
        Assert.Equal("new", existing.Value);
        Assert.Equal("actor", existing.UpdateBy);
        Assert.Equal(UtcNow, existing.UpdateTime);
        Assert.Same(kept, FindCredential(connection, IntegrationCredentialSlots.ConnectionField("kept")));
        Assert.Null(FindCredential(connection, IntegrationCredentialSlots.ConnectionField("cleared")));
        var added = FindCredential(connection, IntegrationCredentialSlots.ConnectionField("added"))!;
        Assert.Equal(Guid.Empty, added.Id);
        Assert.Equal(connection.Id, added.ConnectionId);
        Assert.Same(connection, added.Connection);
        Assert.Equal("created", added.Value);
        Assert.Equal("actor", added.CreateBy);
        Assert.Equal(UtcNow, added.CreateTime);
    }

    [Fact]
    public void HasRequiredConfiguration_RequiredFields_ChecksSecretSlotsAndValues()
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);
        IReadOnlyList<FormFieldDefinition> fields =
        [
            new()
            {
                Id = "token",
                Label = "Token",
                Type = FormFieldType.Secret,
                IsRequired = true,
            },
            new()
            {
                Id = "url",
                Label = "Url",
                Type = FormFieldType.Url,
                IsRequired = true,
            },
            new()
            {
                Id = "note",
                Label = "Note",
                Type = FormFieldType.Text,
            },
        ];
        var configuration = new Dictionary<string, string?> { ["url"] = "https://example.com" };
        var behavior = new ConnectionBehavior(connection);

        Assert.False(behavior.HasRequiredConfiguration(fields, configuration));
        AddCredential(connection, IntegrationCredentialSlots.ConnectionField("token"), "secret");
        Assert.True(behavior.HasRequiredConfiguration(fields, configuration));
        Assert.False(behavior.HasRequiredConfiguration(fields, new Dictionary<string, string?> { ["url"] = " " }));
    }

    [Theory]
    [InlineData(false, true, AuthSchemeType.ApiKey, false, ConnectionStatus.Disabled, null)]
    [InlineData(
        true,
        false,
        AuthSchemeType.ApiKey,
        false,
        ConnectionStatus.NeedsConfiguration,
        "integration.needs_configuration"
    )]
    [InlineData(
        true,
        true,
        AuthSchemeType.OAuth2,
        false,
        ConnectionStatus.PendingAuthorization,
        "integration.pending_authorization"
    )]
    [InlineData(true, true, AuthSchemeType.OAuth2, true, ConnectionStatus.Unverified, null)]
    [InlineData(true, true, AuthSchemeType.ApiKey, false, ConnectionStatus.Unverified, null)]
    public void ApplyConfigurationStatus_Inputs_SetsStatusAndErrorCode(
        bool enabled,
        bool installationConfigured,
        AuthSchemeType authSchemeType,
        bool hasAccessToken,
        ConnectionStatus expectedStatus,
        string? expectedErrorCode
    )
    {
        var connection = CreateConnection(authSchemeType);
        connection.Enabled = enabled;
        connection.LastValidationErrorCode = "previous";
        if (hasAccessToken)
        {
            AddCredential(connection, IntegrationCredentialSlots.OAuthAccessToken, "access");
        }

        new ConnectionBehavior(connection).ApplyConfigurationStatus(installationConfigured, authSchemeType);

        Assert.Equal(expectedStatus, connection.Status);
        Assert.Equal(expectedErrorCode, connection.LastValidationErrorCode);
    }

    [Fact]
    public void CancelPendingAuthorization_AttemptExists_RemovesOnlyAttempt()
    {
        var connection = CreateConnection(AuthSchemeType.OAuth2);
        AddCredential(connection, IntegrationCredentialSlots.OAuthAuthorizationAttempt, "verifier");
        var accessToken = AddCredential(connection, IntegrationCredentialSlots.OAuthAccessToken, "access");

        new ConnectionBehavior(connection).CancelPendingAuthorization();

        Assert.Same(accessToken, Assert.Single(connection.Credentials));
    }

    [Fact]
    public void ResetAfterInstallationChange_ValidatedConnection_ClearsValidationAndAuthorizationAttempt()
    {
        var connection = CreateConnection(AuthSchemeType.OAuth2);
        connection.Status = ConnectionStatus.Ready;
        connection.LastValidatedAtUtc = UtcNow;
        connection.ValidationMetadataJson = "{}";
        AddCredential(connection, IntegrationCredentialSlots.OAuthAuthorizationAttempt, "verifier");

        new ConnectionBehavior(connection).ResetAfterInstallationChange(false, AuthSchemeType.OAuth2);

        Assert.Empty(connection.Credentials);
        Assert.Null(connection.LastValidatedAtUtc);
        Assert.Null(connection.ValidationMetadataJson);
        Assert.Equal(ConnectionStatus.NeedsConfiguration, connection.Status);
        Assert.Equal("integration.needs_configuration", connection.LastValidationErrorCode);
    }

    [Fact]
    public void GetAccessTokenStatus_TokenStates_ReturnsStatusDecidedByToken()
    {
        var connection = CreateConnection(AuthSchemeType.OAuth2);
        var behavior = new ConnectionBehavior(connection);

        Assert.Equal(ConnectionStatus.PendingAuthorization, behavior.GetAccessTokenStatus(UtcNow));

        var accessToken = AddCredential(connection, IntegrationCredentialSlots.OAuthAccessToken, "access");
        Assert.Null(behavior.GetAccessTokenStatus(UtcNow));

        accessToken.ExpiresAtUtc = UtcNow.AddMinutes(1);
        Assert.Null(behavior.GetAccessTokenStatus(UtcNow));

        accessToken.ExpiresAtUtc = UtcNow;
        Assert.Equal(ConnectionStatus.Expired, behavior.GetAccessTokenStatus(UtcNow));
    }

    [Theory]
    [InlineData(false, true, ConnectionStatus.Disabled)]
    [InlineData(true, false, ConnectionStatus.DefinitionUnavailable)]
    [InlineData(true, true, ConnectionStatus.Ready)]
    public void GetEffectiveStatus_Inputs_ReturnsEffectiveStatus(
        bool enabled,
        bool definitionAvailable,
        ConnectionStatus expected
    )
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);
        connection.Enabled = enabled;
        connection.Status = ConnectionStatus.Ready;

        Assert.Equal(expected, new ConnectionBehavior(connection).GetEffectiveStatus(definitionAvailable));
    }

    [Theory]
    [InlineData(ConnectionStatus.Invalid, "integration.credential_invalid")]
    [InlineData(ConnectionStatus.Expired, "integration.credential_expired")]
    [InlineData(ConnectionStatus.DefinitionUnavailable, "integration.definition_unavailable")]
    [InlineData(ConnectionStatus.Ready, null)]
    public void RecordValidation_Status_SetsValidationTimeAndErrorCode(
        ConnectionStatus status,
        string? expectedErrorCode
    )
    {
        var connection = CreateConnection(AuthSchemeType.ApiKey);

        new ConnectionBehavior(connection).RecordValidation(status, UtcNow);

        Assert.Equal(status, connection.Status);
        Assert.Equal(UtcNow, connection.LastValidatedAtUtc);
        Assert.Equal(expectedErrorCode, connection.LastValidationErrorCode);
    }

    [Fact]
    public void AuthorizationTransitions_BeginAuthorizedFailed_SetStatusValidationTimeAndErrorCode()
    {
        var connection = CreateConnection(AuthSchemeType.OAuth2);
        connection.LastValidatedAtUtc = UtcNow.AddDays(-1);
        var behavior = new ConnectionBehavior(connection);

        behavior.BeginAuthorization();
        Assert.Equal(ConnectionStatus.PendingAuthorization, connection.Status);
        Assert.Null(connection.LastValidatedAtUtc);
        Assert.Equal("integration.pending_authorization", connection.LastValidationErrorCode);

        behavior.MarkAuthorized(UtcNow);
        Assert.Equal(ConnectionStatus.Ready, connection.Status);
        Assert.Equal(UtcNow, connection.LastValidatedAtUtc);
        Assert.Null(connection.LastValidationErrorCode);

        behavior.MarkAuthorizationFailed(
            ConnectionStatus.PendingAuthorization,
            "integration.oauth_authorization_denied"
        );
        Assert.Equal(ConnectionStatus.PendingAuthorization, connection.Status);
        Assert.Null(connection.LastValidatedAtUtc);
        Assert.Equal("integration.oauth_authorization_denied", connection.LastValidationErrorCode);
    }

    [Theory]
    [InlineData(null, "existing")]
    [InlineData(" ", "existing")]
    [InlineData("new-subject", "new-subject")]
    public void RefreshSubject_ProviderSubject_KeepsExistingWhenMissing(string? subject, string expected)
    {
        var connection = CreateConnection(AuthSchemeType.OAuth2);
        connection.Subject = "existing";

        new ConnectionBehavior(connection).RefreshSubject(subject);

        Assert.Equal(expected, connection.Subject);
    }

    private static ResolvedIntegrationDefinition CreateDefinition(
        AuthSchemeType authSchemeType,
        string authSchemeId = "scheme"
    )
    {
        var authScheme = new AuthSchemeDefinition
        {
            Id = authSchemeId,
            DisplayName = "Scheme",
            Type = authSchemeType,
        };
        var connector = new ConnectorDefinition
        {
            Id = "connector",
            DisplayName = "Connector",
            AuthSchemes = [authScheme],
        };
        var plugin = new PluginDefinition
        {
            Id = "plugin",
            Version = "1.0.0",
            DisplayName = "Plugin",
            Connectors = [connector],
        };
        return new ResolvedIntegrationDefinition(plugin, connector, authScheme);
    }

    private static Connection CreateConnection(AuthSchemeType authSchemeType)
    {
        var connection = new Connection { Id = Guid.CreateVersion7() };
        new ConnectionBehavior(connection).Create(CreateDefinition(authSchemeType), "work", "Work", true);
        return connection;
    }

    private static ConnectionCredential AddCredential(Connection connection, string slot, string value)
    {
        var credential = new ConnectionCredential
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = connection.Id,
            Connection = connection,
            Slot = slot,
            Value = value,
        };
        connection.Credentials.Add(credential);
        return credential;
    }

    private static ConnectionCredential? FindCredential(Connection connection, string slot) =>
        connection.Credentials.SingleOrDefault(credential => credential.Slot == slot);
}
