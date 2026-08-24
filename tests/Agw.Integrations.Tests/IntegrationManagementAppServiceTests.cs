using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Infrastructure.Plugins;
using Agw.Shared;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Tests;

public class IntegrationManagementAppServiceTests
{
    [Fact]
    public async Task UpsertInstallation_EncryptedSecret_PersistsProtectedValueAndReturnsOnlyState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        const string plaintext = "installation-secret-value";

        var response = await scope.Installations.UpsertAsync(
            new PluginInstallationUpsertRequest
            {
                PluginId = "TEST-PLUGIN",
                ConnectorId = "api",
                AuthSchemeId = "api-key",
                Configuration = new Dictionary<string, string?> { ["client-id"] = "client-123" },
                Secrets = new Dictionary<string, SecretFieldUpdateRequest> { ["client-secret"] = Encrypted(plaintext) },
            },
            Constants.AdminUserId,
            cancellationToken
        );

        Assert.Equal("test-plugin", response.PluginId);
        Assert.Equal("client-123", response.Configuration["client-id"]);
        var secret = response.Secrets["client-secret"];
        Assert.True(secret.Configured);
        Assert.Null(secret.GetType().GetProperty("Value"));
        Assert.Null(secret.GetType().GetProperty("ProtectedValue"));

        scope.DbContext.ChangeTracker.Clear();
        var stored = await scope.DbContext.PluginInstallationCredentials.SingleAsync(cancellationToken);
        Assert.Equal(plaintext, stored.Value);
        Assert.Equal("field:api:api-key:client-secret", stored.Slot);

        var plugins = await scope.Plugins.ListAsync(cancellationToken);
        var installation = Assert.Single(Assert.Single(plugins).Connectors[0].AuthSchemes).Installation;
        Assert.NotNull(installation);
        Assert.Equal("client-123", installation.Configuration["client-id"]);
        Assert.True(installation.Secrets["client-secret"].Configured);
        Assert.Null(installation.Secrets["client-secret"].GetType().GetProperty("SecretValue"));
    }

    [Fact]
    public void SecretContracts_ExposeOnlyEncryptedValueStorage()
    {
        Assert.Null(typeof(SecretFieldUpdateRequest).GetProperty("Source"));
        Assert.Null(typeof(SecretFieldUpdateRequest).GetProperty("EnvironmentVariableName"));
        Assert.Null(typeof(SecretFieldStateResponse).GetProperty("Source"));
        Assert.Null(typeof(SecretFieldStateResponse).GetProperty("DisplayHint"));
        Assert.Null(typeof(PluginInstallationCredential).GetProperty("DisplayHint"));
        Assert.Null(typeof(ConnectionCredential).GetProperty("DisplayHint"));
    }

    [Fact]
    public async Task ConnectionOperations_UserScope_IsolatesDataAndAllowsAliasReuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var first = await scope.Connections.CreateAsync(ConnectionRequest("shared-alias"), "user-a", cancellationToken);
        var second = await scope.Connections.CreateAsync(
            ConnectionRequest("shared-alias"),
            "user-b",
            cancellationToken
        );

        Assert.Equal(first.Id, Assert.Single(await scope.Connections.ListAsync(null, "user-a", cancellationToken)).Id);
        Assert.Equal(second.Id, Assert.Single(await scope.Connections.ListAsync(null, "user-b", cancellationToken)).Id);
        Assert.Empty(await scope.Connections.ListAsync(first.Id, "user-b", cancellationToken));
        Assert.Null(
            await scope.Reader.ReadConnectionAsync(
                first.Id,
                "user-b",
                IntegrationCredentialSlots.ConnectionField("api-key"),
                cancellationToken
            )
        );
        var duplicate = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.CreateAsync(ConnectionRequest("shared-alias"), "user-a", cancellationToken)
        );
        Assert.Equal(ErrorCodes.ConnectionAliasAlreadyExists.Code, duplicate.Code);
        var foreignValidation = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.ValidateAsync(first.Id, "user-b", cancellationToken)
        );
        Assert.Equal(ErrorCodes.ConnectionNotFound.Code, foreignValidation.Code);
        Assert.False(await scope.Connections.DeleteAsync(first.Id, "user-b", cancellationToken));
        Assert.True(await scope.Connections.DeleteAsync(first.Id, "user-a", cancellationToken));
    }

    [Fact]
    public async Task UpsertInstallation_NonAdministrator_ThrowsAdministratorRequired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(
                InstallationRequest(Encrypted("replacement"), null),
                "user-a",
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.AdministratorRequired.Code, exception.Code);
    }

    [Fact]
    public async Task UpsertInstallation_KeepSetAndClear_AppliesMutationSemantics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        var initial = InstallationRequest(Encrypted("initial-secret"), Encrypted("optional-secret"));
        var created = await scope.Installations.UpsertAsync(initial, Constants.AdminUserId, cancellationToken);

        var credentialBeforeKeep = await scope.DbContext.PluginInstallationCredentials.SingleAsync(
            item => item.Slot.EndsWith("client-secret"),
            cancellationToken
        );
        var valueBeforeKeep = credentialBeforeKeep.Value;

        scope.DbContext.ChangeTracker.Clear();
        var kept = await scope.Installations.UpsertAsync(
            new PluginInstallationUpsertRequest
            {
                PluginId = "test-plugin",
                ConnectorId = "api",
                AuthSchemeId = "api-key",
                Configuration = new Dictionary<string, string?> { ["client-id"] = "client-456" },
                Secrets = new Dictionary<string, SecretFieldUpdateRequest>
                {
                    ["client-secret"] = new SecretFieldUpdateRequest { Action = SecretUpdateAction.Keep },
                    ["optional-secret"] = Encrypted("optional-replacement"),
                },
            },
            Constants.AdminUserId,
            cancellationToken
        );

        Assert.Equal("client-456", kept.Configuration["client-id"]);
        Assert.True(kept.Secrets["optional-secret"].Configured);
        var resolvedOptional = await scope.Reader.ReadPluginInstallationAsync(
            kept.Id,
            IntegrationCredentialSlots.InstallationField("api", "api-key", "optional-secret"),
            cancellationToken
        );
        Assert.Equal("optional-replacement", resolvedOptional!.Value);

        scope.DbContext.ChangeTracker.Clear();
        var credentialAfterKeep = await scope.DbContext.PluginInstallationCredentials.SingleAsync(
            item => item.Slot.EndsWith("client-secret"),
            cancellationToken
        );
        Assert.Equal(valueBeforeKeep, credentialAfterKeep.Value);

        await scope.Installations.UpsertAsync(
            new PluginInstallationUpsertRequest
            {
                PluginId = created.PluginId,
                ConnectorId = created.ConnectorId,
                AuthSchemeId = created.AuthSchemeId,
                Configuration = new Dictionary<string, string?> { ["client-id"] = "client-456" },
                Secrets = new Dictionary<string, SecretFieldUpdateRequest>
                {
                    ["client-secret"] = Encrypted("replacement-secret"),
                    ["optional-secret"] = new SecretFieldUpdateRequest { Action = SecretUpdateAction.Clear },
                },
            },
            Constants.AdminUserId,
            cancellationToken
        );

        scope.DbContext.ChangeTracker.Clear();
        var credentials = await scope.DbContext.PluginInstallationCredentials.ToListAsync(cancellationToken);
        var replacement = Assert.Single(credentials);
        Assert.Equal("replacement-secret", replacement.Value);
        Assert.EndsWith("client-secret", replacement.Slot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertInstallation_InvalidFieldsOrSecretSource_ThrowsStableError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var missingRequired = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(
                new PluginInstallationUpsertRequest
                {
                    PluginId = "test-plugin",
                    ConnectorId = "api",
                    AuthSchemeId = "api-key",
                    Secrets = new Dictionary<string, SecretFieldUpdateRequest>
                    {
                        ["client-secret"] = Encrypted("secret"),
                    },
                },
                Constants.AdminUserId,
                cancellationToken
            )
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, missingRequired.Code);

        var unknown = InstallationRequest(Encrypted("secret"), null);
        unknown.Configuration["unknown"] = "value";
        var unknownError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(unknown, Constants.AdminUserId, cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, unknownError.Code);

        var invalidSource = InstallationRequest(
            new SecretFieldUpdateRequest { Action = SecretUpdateAction.Keep, SecretValue = "MUST_NOT_BE_SET" },
            null
        );
        var sourceError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(invalidSource, Constants.AdminUserId, cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationSecretMutationInvalid.Code, sourceError.Code);
    }

    [Fact]
    public async Task Connection_CreateUpdate_EnforcesUniqueImmutableNormalizedAlias()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var created = await scope.Connections.CreateAsync(ConnectionRequest("WORK"), "tester", cancellationToken);
        Assert.Equal("work", created.Alias);
        Assert.Equal(ConnectionStatusResponse.Unverified, created.Status);

        var duplicateError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.CreateAsync(ConnectionRequest("work"), "tester", cancellationToken)
        );
        Assert.Equal(ErrorCodes.ConnectionAliasAlreadyExists.Code, duplicateError.Code);

        var immutableError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.UpdateAsync(
                new ConnectionUpdateRequest
                {
                    Id = created.Id,
                    PluginId = created.PluginId,
                    ConnectorId = created.ConnectorId,
                    AuthSchemeId = created.AuthSchemeId,
                    Alias = "personal",
                    DisplayName = "Work",
                    Enabled = true,
                    Configuration = new Dictionary<string, string?> { ["endpoint"] = "https://api.example.test" },
                },
                "tester",
                cancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ConnectionAliasImmutable.Code, immutableError.Code);

        var identityError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.UpdateAsync(
                new ConnectionUpdateRequest
                {
                    Id = created.Id,
                    PluginId = created.PluginId,
                    ConnectorId = "oauth",
                    AuthSchemeId = "oauth2",
                    Alias = created.Alias,
                    DisplayName = "Work",
                    Enabled = true,
                },
                "tester",
                cancellationToken
            )
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, identityError.Code);
    }

    [Fact]
    public async Task Connection_CreateWithMissingOrUnknownFields_ThrowsStableError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var missing = ConnectionRequest("missing-field");
        missing.Secrets.Clear();
        var missingError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.CreateAsync(missing, "tester", cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, missingError.Code);

        var unknown = ConnectionRequest("unknown-field");
        unknown.Configuration["unknown"] = "value";
        var unknownError = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.CreateAsync(unknown, "tester", cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, unknownError.Code);
    }

    [Fact]
    public async Task CredentialReader_EncryptedCredential_ResolvesWithoutExposingStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);

        var connection = await scope.Connections.CreateAsync(
            ConnectionRequest("encrypted"),
            "tester",
            cancellationToken
        );

        var value = await scope.Reader.ReadConnectionAsync(
            connection.Id,
            "tester",
            IntegrationCredentialSlots.ConnectionField("api-key"),
            cancellationToken
        );
        var stored = await scope.DbContext.ConnectionCredentials.SingleAsync(
            credential => credential.ConnectionId == connection.Id,
            cancellationToken
        );

        Assert.Equal("connection-secret", value!.Value);
        Assert.Equal("connection-secret", stored.Value);
    }

    [Fact]
    public async Task ValidateConnection_LocalConfiguration_ResolvesAllStatuses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken, now);

        var readyCandidate = await scope.Connections.CreateAsync(
            ConnectionRequest("ready"),
            "tester",
            cancellationToken
        );
        var ready = await scope.Connections.ValidateAsync(readyCandidate.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Ready, ready.Status);
        Assert.Equal(now, ready.LastValidatedAtUtc);

        var invalidCandidate = await scope.Connections.CreateAsync(
            ConnectionRequest("invalid"),
            "tester",
            cancellationToken
        );
        var invalidCredential = await scope.DbContext.ConnectionCredentials.SingleAsync(
            credential => credential.ConnectionId == invalidCandidate.Id,
            cancellationToken
        );
        scope.DbContext.ChangeTracker.Clear();
        const string invalidProtectedValue = "invalid-protected-value";
        await scope.DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE integration_connection_credential SET protected_value = {invalidProtectedValue} WHERE id = {invalidCredential.Id}",
            cancellationToken
        );
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Connections.ValidateAsync(invalidCandidate.Id, "tester", cancellationToken)
        );
        Assert.Equal(ErrorCodes.EncryptedDataInvalid.Code, exception.Code);

        var disabledRequest = ConnectionRequest("disabled");
        disabledRequest.Enabled = false;
        var disabled = await scope.Connections.CreateAsync(disabledRequest, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Disabled, disabled.Status);

        var oauth = await scope.Connections.CreateAsync(OAuthConnectionRequest("oauth"), "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.PendingAuthorization, oauth.Status);

        scope.DbContext.ConnectionCredentials.Add(
            new ConnectionCredential
            {
                Id = Guid.CreateVersion7(),
                ConnectionId = oauth.Id,
                Slot = IntegrationCredentialSlots.OAuthAccessToken,
                Value = "oauth-token",
                ExpiresAtUtc = now.AddMinutes(-1),
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        var expired = await scope.Connections.ValidateAsync(oauth.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Expired, expired.Status);

        var token = await scope.DbContext.ConnectionCredentials.SingleAsync(
            item => item.ConnectionId == oauth.Id,
            cancellationToken
        );
        token.ExpiresAtUtc = now.AddMinutes(30);
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        var oauthReady = await scope.Connections.ValidateAsync(oauth.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Ready, oauthReady.Status);

        scope.DbContext.Connections.Add(
            new IntegrationConnection
            {
                Id = Guid.CreateVersion7(),
                PluginId = "missing-plugin",
                ConnectorId = "missing",
                AuthSchemeId = "missing",
                Alias = "definition-unavailable",
                DisplayName = "Missing",
                ConfigurationJson = "{}",
                Enabled = true,
                Status = ConnectionStatus.Unverified,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        var unavailableEntity = await scope.DbContext.Connections.SingleAsync(
            item => item.Alias == "definition-unavailable",
            cancellationToken
        );
        scope.DbContext.ChangeTracker.Clear();

        var unavailable = await scope.Connections.ValidateAsync(unavailableEntity.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.DefinitionUnavailable, unavailable.Status);
    }

    [Fact]
    public async Task GitHubConnection_InstallationMissingOrDisabled_NeedsConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(
            cancellationToken,
            catalog: new BuiltInPluginCatalog()
        );
        var request = new ConnectionCreateRequest
        {
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth2",
            Alias = "github-missing",
            DisplayName = "GitHub missing",
        };

        var missing = await scope.Connections.CreateAsync(request, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, missing.Status);

        await scope.Installations.UpsertAsync(
            new PluginInstallationUpsertRequest
            {
                PluginId = "github",
                ConnectorId = "github-cloud",
                AuthSchemeId = "oauth2",
                Enabled = true,
                Configuration = new Dictionary<string, string?> { ["client-id"] = "github-client" },
                Secrets = new Dictionary<string, SecretFieldUpdateRequest>
                {
                    ["client-secret"] = Encrypted("github-secret"),
                },
            },
            Constants.AdminUserId,
            cancellationToken
        );
        request.Alias = "github-ready";
        request.DisplayName = "GitHub ready";
        var pending = await scope.Connections.CreateAsync(request, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.PendingAuthorization, pending.Status);

        var installation = await scope.DbContext.PluginInstallations.SingleAsync(cancellationToken);
        installation.Enabled = false;
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        var disabled = await scope.Connections.ValidateAsync(pending.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, disabled.Status);
    }

    [Fact]
    public async Task UpsertInstallation_ClearRequiredSecret_InvalidatesReadyConnectionWithoutValidation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken, now);
        var created = await scope.Connections.CreateAsync(
            ConnectionRequest("clear-required"),
            "tester",
            cancellationToken
        );
        var ready = await scope.Connections.ValidateAsync(created.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Ready, ready.Status);
        Assert.Equal(now, ready.LastValidatedAtUtc);
        var stored = await scope.DbContext.Connections.SingleAsync(
            connection => connection.Id == created.Id,
            cancellationToken
        );
        stored.ValidationMetadataJson = "{\"state\":\"stale\"}";
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        await scope.Installations.UpsertAsync(
            InstallationRequest(new SecretFieldUpdateRequest { Action = SecretUpdateAction.Clear }, null),
            Constants.AdminUserId,
            cancellationToken
        );

        var connection = Assert.Single(await scope.Connections.ListAsync(created.Id, "tester", cancellationToken));
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, connection.Status);
        Assert.Null(connection.LastValidatedAtUtc);
        Assert.Equal("integration.needs_configuration", connection.LastValidationErrorCode);
        var invalidated = await scope.DbContext.Connections.SingleAsync(
            item => item.Id == created.Id,
            cancellationToken
        );
        Assert.Null(invalidated.ValidationMetadataJson);
    }

    [Fact]
    public async Task UpsertInstallation_DisableAfterRequiredSecretCleared_SkipsRequiredPresenceValidation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        var connection = await scope.Connections.CreateAsync(
            ConnectionRequest("disable-after-clear"),
            "tester",
            cancellationToken
        );
        await scope.Installations.UpsertAsync(
            InstallationRequest(new SecretFieldUpdateRequest { Action = SecretUpdateAction.Clear }, null),
            Constants.AdminUserId,
            cancellationToken
        );

        var disabled = await scope.Installations.UpsertAsync(
            new PluginInstallationUpsertRequest
            {
                PluginId = "test-plugin",
                ConnectorId = "api",
                AuthSchemeId = "api-key",
                Enabled = false,
            },
            Constants.AdminUserId,
            cancellationToken
        );

        Assert.False(disabled.Enabled);
        var response = Assert.Single(await scope.Connections.ListAsync(connection.Id, "tester", cancellationToken));
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, response.Status);
    }

    [Fact]
    public async Task UpsertInstallation_Disabled_StillValidatesUnknownFieldsAndSecretShape()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        var request = new PluginInstallationUpsertRequest
        {
            PluginId = "test-plugin",
            ConnectorId = "api",
            AuthSchemeId = "api-key",
            Enabled = false,
            Configuration = new Dictionary<string, string?> { ["unknown"] = "value" },
        };

        var unknown = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(request, Constants.AdminUserId, cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationConfigurationInvalid.Code, unknown.Code);

        request.Configuration.Clear();
        request.Secrets["client-secret"] = new SecretFieldUpdateRequest
        {
            Action = SecretUpdateAction.Set,
            SecretValue = " ",
        };
        var invalidSecret = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Installations.UpsertAsync(request, Constants.AdminUserId, cancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationSecretMutationInvalid.Code, invalidSecret.Code);
    }

    [Fact]
    public async Task UpsertInstallation_Disable_InvalidatesAllPluginConnectionsAndPreservesDisabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        var api = await scope.Connections.CreateAsync(ConnectionRequest("api-ready"), "tester", cancellationToken);
        var ready = await scope.Connections.ValidateAsync(api.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Ready, ready.Status);
        var storedReady = await scope.DbContext.Connections.SingleAsync(
            connection => connection.Id == api.Id,
            cancellationToken
        );
        storedReady.ValidationMetadataJson = "{\"state\":\"stale\"}";
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        var oauth = await scope.Connections.CreateAsync(
            OAuthConnectionRequest("oauth-pending"),
            "tester",
            cancellationToken
        );
        Assert.Equal(ConnectionStatusResponse.PendingAuthorization, oauth.Status);

        var disabledRequest = ConnectionRequest("api-disabled");
        disabledRequest.Enabled = false;
        var disabled = await scope.Connections.CreateAsync(disabledRequest, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Disabled, disabled.Status);

        var disableInstallation = InstallationRequest(
            new SecretFieldUpdateRequest { Action = SecretUpdateAction.Keep },
            null
        );
        disableInstallation.Enabled = false;
        await scope.Installations.UpsertAsync(disableInstallation, Constants.AdminUserId, cancellationToken);

        var connections = (await scope.Connections.ListAsync(null, "tester", cancellationToken)).ToDictionary(
            connection => connection.Alias,
            StringComparer.Ordinal
        );
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, connections["api-ready"].Status);
        Assert.Equal(ConnectionStatusResponse.NeedsConfiguration, connections["oauth-pending"].Status);
        Assert.Equal(ConnectionStatusResponse.Disabled, connections["api-disabled"].Status);
        Assert.Null(connections["api-ready"].LastValidatedAtUtc);
        Assert.Equal("integration.needs_configuration", connections["api-ready"].LastValidationErrorCode);
        Assert.Equal("integration.needs_configuration", connections["oauth-pending"].LastValidationErrorCode);
        Assert.Null(connections["api-disabled"].LastValidationErrorCode);
        var invalidatedReady = await scope.DbContext.Connections.SingleAsync(
            connection => connection.Id == api.Id,
            cancellationToken
        );
        Assert.Null(invalidatedReady.ValidationMetadataJson);
    }

    [Fact]
    public async Task UpsertInstallation_RotateOAuthConfiguration_ResetsStatusesByAuthorizationState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        await using var scope = await ManagementTestScope.CreateAsync(
            cancellationToken,
            now,
            new BuiltInPluginCatalog()
        );
        var installation = new PluginInstallationUpsertRequest
        {
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth2",
            Enabled = true,
            Configuration = new Dictionary<string, string?> { ["client-id"] = "github-client" },
            Secrets = new Dictionary<string, SecretFieldUpdateRequest>
            {
                ["client-secret"] = Encrypted("github-secret"),
            },
        };
        await scope.Installations.UpsertAsync(installation, Constants.AdminUserId, cancellationToken);

        var pending = await scope.Connections.CreateAsync(
            new ConnectionCreateRequest
            {
                PluginId = "github",
                ConnectorId = "github-cloud",
                AuthSchemeId = "oauth2",
                Alias = "github-pending",
                DisplayName = "GitHub pending",
            },
            "tester",
            cancellationToken
        );
        Assert.Equal(ConnectionStatusResponse.PendingAuthorization, pending.Status);

        var tokenConnection = await scope.Connections.CreateAsync(
            new ConnectionCreateRequest
            {
                PluginId = "github",
                ConnectorId = "github-cloud",
                AuthSchemeId = "oauth2",
                Alias = "github-token",
                DisplayName = "GitHub token",
            },
            "tester",
            cancellationToken
        );
        scope.DbContext.ConnectionCredentials.Add(
            new ConnectionCredential
            {
                Id = Guid.CreateVersion7(),
                ConnectionId = tokenConnection.Id,
                Slot = IntegrationCredentialSlots.OAuthAccessToken,
                Value = "oauth-token",
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();
        var ready = await scope.Connections.ValidateAsync(tokenConnection.Id, "tester", cancellationToken);
        Assert.Equal(ConnectionStatusResponse.Ready, ready.Status);
        var storedReady = await scope.DbContext.Connections.SingleAsync(
            connection => connection.Id == tokenConnection.Id,
            cancellationToken
        );
        storedReady.ValidationMetadataJson = "{\"state\":\"stale\"}";
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        var disabled = await scope.Connections.CreateAsync(
            new ConnectionCreateRequest
            {
                PluginId = "github",
                ConnectorId = "github-cloud",
                AuthSchemeId = "oauth2",
                Alias = "github-disabled",
                DisplayName = "GitHub disabled",
                Enabled = false,
            },
            "tester",
            cancellationToken
        );
        Assert.Equal(ConnectionStatusResponse.Disabled, disabled.Status);

        installation.Configuration["client-id"] = "rotated-client";
        installation.Secrets["client-secret"] = Encrypted("rotated-secret");
        await scope.Installations.UpsertAsync(installation, Constants.AdminUserId, cancellationToken);

        var connections = (await scope.Connections.ListAsync(null, "tester", cancellationToken)).ToDictionary(
            connection => connection.Alias,
            StringComparer.Ordinal
        );
        Assert.Equal(ConnectionStatusResponse.PendingAuthorization, connections["github-pending"].Status);
        Assert.Equal(ConnectionStatusResponse.Unverified, connections["github-token"].Status);
        Assert.Equal(ConnectionStatusResponse.Disabled, connections["github-disabled"].Status);
        Assert.Null(connections["github-token"].LastValidatedAtUtc);
        Assert.Null(connections["github-token"].LastValidationErrorCode);
        Assert.Equal("integration.pending_authorization", connections["github-pending"].LastValidationErrorCode);
        Assert.Null(connections["github-disabled"].LastValidationErrorCode);
        var invalidatedReady = await scope.DbContext.Connections.SingleAsync(
            connection => connection.Id == tokenConnection.Id,
            cancellationToken
        );
        Assert.Null(invalidatedReady.ValidationMetadataJson);
    }

    [Fact]
    public async Task DeleteConnection_RemovesConnectionCredentialsAndBindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ManagementTestScope.CreateAsync(cancellationToken);
        var created = await scope.Connections.CreateAsync(ConnectionRequest("delete-me"), "tester", cancellationToken);
        var agent = CreateAgent();
        var project = CreateProject();
        scope.DbContext.Agents.Add(agent);
        scope.DbContext.Projects.Add(project);
        scope.DbContext.AgentConnectionRelations.Add(
            new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = created.Id }
        );
        scope.DbContext.ProjectConnectionRelations.Add(
            new ProjectConnectionRelation { ProjectId = project.Id, ConnectionId = created.Id }
        );
        await scope.DbContext.SaveChangesAsync(cancellationToken);
        scope.DbContext.ChangeTracker.Clear();

        Assert.True(await scope.Connections.DeleteAsync(created.Id, "tester", cancellationToken));

        Assert.False(await scope.DbContext.Connections.AnyAsync(item => item.Id == created.Id, cancellationToken));
        Assert.False(
            await scope.DbContext.ConnectionCredentials.AnyAsync(
                item => item.ConnectionId == created.Id,
                cancellationToken
            )
        );
        Assert.False(
            await scope.DbContext.AgentConnectionRelations.AnyAsync(
                item => item.ConnectionId == created.Id,
                cancellationToken
            )
        );
        Assert.False(
            await scope.DbContext.ProjectConnectionRelations.AnyAsync(
                item => item.ConnectionId == created.Id,
                cancellationToken
            )
        );
    }

    private static PluginInstallationUpsertRequest InstallationRequest(
        SecretFieldUpdateRequest clientSecret,
        SecretFieldUpdateRequest? optionalSecret
    )
    {
        var secrets = new Dictionary<string, SecretFieldUpdateRequest> { ["client-secret"] = clientSecret };
        if (optionalSecret != null)
        {
            secrets["optional-secret"] = optionalSecret;
        }

        return new PluginInstallationUpsertRequest
        {
            PluginId = "test-plugin",
            ConnectorId = "api",
            AuthSchemeId = "api-key",
            Configuration = new Dictionary<string, string?> { ["client-id"] = "client-123" },
            Secrets = secrets,
        };
    }

    private static ConnectionCreateRequest ConnectionRequest(string alias) =>
        new()
        {
            PluginId = "test-plugin",
            ConnectorId = "api",
            AuthSchemeId = "api-key",
            Alias = alias,
            DisplayName = alias,
            Enabled = true,
            Configuration = new Dictionary<string, string?> { ["endpoint"] = "https://api.example.test" },
            Secrets = new Dictionary<string, SecretFieldUpdateRequest> { ["api-key"] = Encrypted("connection-secret") },
        };

    private static ConnectionCreateRequest OAuthConnectionRequest(string alias) =>
        new()
        {
            PluginId = "test-plugin",
            ConnectorId = "oauth",
            AuthSchemeId = "oauth2",
            Alias = alias,
            DisplayName = alias,
            Enabled = true,
        };

    private static SecretFieldUpdateRequest Encrypted(string value) =>
        new() { Action = SecretUpdateAction.Set, SecretValue = value };

    private static Agent CreateAgent() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = $"agent-{Guid.CreateVersion7():N}",
            DisplayName = "Agent",
            Description = "Agent",
            SystemPrompt = "Prompt",
            Type = AgentType.System,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static Project CreateProject() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = $"project-{Guid.CreateVersion7():N}",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private sealed class ManagementTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ManagementTestScope(
            SqliteConnection connection,
            AgwDbContext dbContext,
            PluginCatalogAppService plugins,
            TestPluginInstallationClient installations,
            TestConnectionClient connections,
            TestCredentialReader reader
        )
        {
            _connection = connection;
            DbContext = dbContext;
            Plugins = plugins;
            Installations = installations;
            Connections = connections;
            Reader = reader;
        }

        public AgwDbContext DbContext { get; }
        public PluginCatalogAppService Plugins { get; }
        public TestPluginInstallationClient Installations { get; }
        public TestConnectionClient Connections { get; }
        public TestCredentialReader Reader { get; }

        public static async Task<ManagementTestScope> CreateAsync(
            CancellationToken cancellationToken,
            DateTimeOffset? now = null,
            IPluginCatalog? catalog = null
        )
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var encryptedDataProtector = new DataProtectionEncryptedDataProtector(
                new EphemeralDataProtectionProvider()
            );
            var dbContext = new AgwDbContext(options, encryptedDataProtector);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            catalog ??= new TestPluginCatalog();
            var timeProvider = new TestTimeProvider(now ?? TimeProvider.System.GetUtcNow());

            IRepository<PluginInstallation> installationRepository = new EfRepository<PluginInstallation>(dbContext);
            IRepository<PluginInstallationCredential> installationCredentialRepository =
                new EfRepository<PluginInstallationCredential>(dbContext);
            IRepository<IntegrationConnection> connectionRepository = new EfRepository<IntegrationConnection>(
                dbContext
            );
            IRepository<ConnectionCredential> connectionCredentialRepository = new EfRepository<ConnectionCredential>(
                dbContext
            );
            IUnitOfWork unitOfWork = dbContext;
            var userInfo = new TestUserInfoService(Constants.AdminUserId);

            var reader = new ConnectionCredentialReader(
                installationCredentialRepository,
                connectionCredentialRepository,
                userInfo
            );
            var credentialMutations = new CredentialMutationService(
                installationCredentialRepository,
                connectionCredentialRepository,
                timeProvider,
                userInfo
            );
            var installations = new PluginInstallationAppService(
                installationRepository,
                connectionRepository,
                unitOfWork,
                catalog,
                credentialMutations,
                timeProvider,
                userInfo
            );
            var plugins = new PluginCatalogAppService(
                catalog,
                installationRepository,
                new PluginSkillMetadataReader(new AppContextPluginContentRootProvider())
            );
            var connections = new ConnectionAppService(
                connectionRepository,
                installationRepository,
                unitOfWork,
                catalog,
                credentialMutations,
                reader,
                timeProvider,
                userInfo
            );
            var installationClient = new TestPluginInstallationClient(installations, userInfo);
            var connectionClient = new TestConnectionClient(connections, userInfo);
            var credentialReader = new TestCredentialReader(reader, userInfo);

            if (catalog is TestPluginCatalog)
            {
                await installationClient.UpsertAsync(
                    InstallationRequest(Encrypted("seed-secret"), null),
                    Constants.AdminUserId,
                    cancellationToken
                );
                dbContext.ChangeTracker.Clear();
            }

            return new ManagementTestScope(
                connection,
                dbContext,
                plugins,
                installationClient,
                connectionClient,
                credentialReader
            );
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestPluginInstallationClient
    {
        private readonly PluginInstallationAppService _service;
        private readonly TestUserInfoService _userInfo;

        public TestPluginInstallationClient(PluginInstallationAppService service, TestUserInfoService userInfo)
        {
            _service = service;
            _userInfo = userInfo;
        }

        public Task<PluginInstallationResponse> UpsertAsync(
            PluginInstallationUpsertRequest request,
            string userId,
            CancellationToken cancellationToken
        )
        {
            _userInfo.UserId = userId;
            return _service.UpsertAsync(request, cancellationToken);
        }
    }

    private sealed class TestConnectionClient
    {
        private readonly ConnectionAppService _service;
        private readonly TestUserInfoService _userInfo;

        public TestConnectionClient(ConnectionAppService service, TestUserInfoService userInfo)
        {
            _service = service;
            _userInfo = userInfo;
        }

        public Task<IReadOnlyList<ConnectionResponse>> ListAsync(
            Guid? id,
            string userId,
            CancellationToken cancellationToken
        )
        {
            SetUser(userId);
            return _service.ListAsync(id, cancellationToken);
        }

        public Task<ConnectionResponse> CreateAsync(
            ConnectionCreateRequest request,
            string userId,
            CancellationToken cancellationToken
        )
        {
            SetUser(userId);
            return _service.CreateAsync(request, cancellationToken);
        }

        public Task<ConnectionResponse> UpdateAsync(
            ConnectionUpdateRequest request,
            string userId,
            CancellationToken cancellationToken
        )
        {
            SetUser(userId);
            return _service.UpdateAsync(request, cancellationToken);
        }

        public Task<ConnectionResponse> ValidateAsync(Guid id, string userId, CancellationToken cancellationToken)
        {
            SetUser(userId);
            return _service.ValidateAsync(id, cancellationToken);
        }

        public Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken)
        {
            SetUser(userId);
            return _service.DeleteAsync(id, cancellationToken);
        }

        private void SetUser(string userId)
        {
            _userInfo.UserId = userId;
        }
    }

    private sealed class TestCredentialReader
    {
        private readonly IConnectionCredentialReader _reader;
        private readonly TestUserInfoService _userInfo;

        public TestCredentialReader(IConnectionCredentialReader reader, TestUserInfoService userInfo)
        {
            _reader = reader;
            _userInfo = userInfo;
        }

        public Task<ResolvedCredential?> ReadConnectionAsync(
            Guid connectionId,
            string userId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            _userInfo.UserId = userId;
            return _reader.ReadConnectionAsync(connectionId, slot, cancellationToken);
        }

        public Task<ResolvedCredential?> ReadPluginInstallationAsync(
            Guid pluginInstallationId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            return _reader.ReadPluginInstallationAsync(pluginInstallationId, slot, cancellationToken);
        }
    }

    private sealed class TestPluginCatalog : IPluginCatalog
    {
        private static readonly PluginDefinition Plugin = new()
        {
            Id = "test-plugin",
            Version = "1.0.0",
            DisplayName = "Test plugin",
            Connectors =
            [
                new ConnectorDefinition
                {
                    Id = "api",
                    DisplayName = "API",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "api-key",
                            DisplayName = "API key",
                            Type = AuthSchemeType.ApiKey,
                            InstallationFields =
                            [
                                Field("client-id", FormFieldType.Text, true),
                                Field("client-secret", FormFieldType.Secret, true),
                                Field("optional-secret", FormFieldType.Secret, false),
                            ],
                            ConnectionFields =
                            [
                                Field("endpoint", FormFieldType.Url, true),
                                Field("api-key", FormFieldType.Secret, true),
                            ],
                        },
                    ],
                },
                new ConnectorDefinition
                {
                    Id = "oauth",
                    DisplayName = "OAuth",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "oauth2",
                            DisplayName = "OAuth 2.0",
                            Type = AuthSchemeType.OAuth2,
                            OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                            {
                                AuthorizationEndpoint = "https://example.test/authorize",
                                TokenEndpoint = "https://example.test/token",
                                ClientIdFieldId = "client-id",
                                SubjectResolution = new OAuthSubjectResolutionDefinition
                                {
                                    Source = OAuthSubjectSource.TokenResponse,
                                    Field = "subject",
                                },
                                ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.None,
                                UsePkce = true,
                            },
                        },
                    ],
                },
            ],
        };

        public IReadOnlyList<PluginDefinition> List() => [Plugin];

        public PluginDefinition? Find(string pluginId) =>
            string.Equals(pluginId, Plugin.Id, StringComparison.OrdinalIgnoreCase) ? Plugin : null;

        private static FormFieldDefinition Field(string id, FormFieldType type, bool required) =>
            new()
            {
                Id = id,
                Label = id,
                Type = type,
                IsRequired = required,
            };
    }
}
