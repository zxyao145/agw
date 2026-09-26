using Agw.Integrations.Domain.Behaviors;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Tests;

public class PluginInstallationBehaviorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplySecretUpdates_SetAndClear_UsesSlotsScopedToConnectorAndAuthScheme()
    {
        var installation = new PluginInstallation { Id = Guid.CreateVersion7(), PluginId = "plugin" };
        var otherScheme = AddCredential(
            installation,
            IntegrationCredentialSlots.InstallationField("connector", "other", "client_secret"),
            "other"
        );
        AddCredential(
            installation,
            IntegrationCredentialSlots.InstallationField("connector", "scheme", "cleared"),
            "value"
        );

        new PluginInstallationBehavior(installation).ApplySecretUpdates(
            CreateDefinition(),
            new Dictionary<string, string> { ["client_secret"] = "secret" },
            ["cleared"],
            "actor",
            UtcNow
        );

        Assert.Equal(2, installation.Credentials.Count);
        Assert.Contains(otherScheme, installation.Credentials);
        var added = installation.Credentials.Single(credential =>
            credential.Slot == IntegrationCredentialSlots.InstallationField("connector", "scheme", "client_secret")
        );
        Assert.Equal(Guid.Empty, added.Id);
        Assert.Equal(installation.Id, added.PluginInstallationId);
        Assert.Equal("secret", added.Value);
        Assert.Equal("actor", added.CreateBy);
        Assert.Equal(UtcNow, added.CreateTime);
    }

    [Fact]
    public void IsConfiguredFor_RequiredFields_ChecksScopedSecretSlotAndValue()
    {
        var installation = new PluginInstallation { Id = Guid.CreateVersion7(), PluginId = "plugin" };
        var definition = CreateDefinition();
        var behavior = new PluginInstallationBehavior(installation);
        var configuration = new Dictionary<string, string?> { ["client_id"] = "client" };
        AddCredential(
            installation,
            IntegrationCredentialSlots.InstallationField("connector", "other", "client_secret"),
            "other"
        );

        Assert.False(behavior.IsConfiguredFor(definition, configuration));

        AddCredential(
            installation,
            IntegrationCredentialSlots.InstallationField("connector", "scheme", "client_secret"),
            "secret"
        );
        Assert.True(behavior.IsConfiguredFor(definition, configuration));
        Assert.False(behavior.IsConfiguredFor(definition, new Dictionary<string, string?> { ["client_id"] = " " }));
    }

    private static ResolvedIntegrationDefinition CreateDefinition()
    {
        var authScheme = new AuthSchemeDefinition
        {
            Id = "scheme",
            DisplayName = "Scheme",
            Type = AuthSchemeType.OAuth2,
            InstallationFields =
            [
                new FormFieldDefinition
                {
                    Id = "client_id",
                    Label = "Client ID",
                    Type = FormFieldType.Text,
                    IsRequired = true,
                },
                new FormFieldDefinition
                {
                    Id = "client_secret",
                    Label = "Client secret",
                    Type = FormFieldType.Secret,
                    IsRequired = true,
                },
            ],
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

    private static PluginInstallationCredential AddCredential(
        PluginInstallation installation,
        string slot,
        string value
    )
    {
        var credential = new PluginInstallationCredential
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = installation.Id,
            PluginInstallation = installation,
            Slot = slot,
            Value = value,
        };
        installation.Credentials.Add(credential);
        return credential;
    }
}
