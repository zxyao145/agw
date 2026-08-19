using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Infrastructure.Plugins;

namespace Agw.Integrations.Tests;

public class BuiltInPluginCatalogTests
{
    [Fact]
    public void PluginSkillDefinition_UsesSkillMarkdownAsMetadataSource()
    {
        Assert.Null(typeof(PluginSkillDefinition).GetProperty("Id"));
        Assert.Null(typeof(PluginSkillDefinition).GetProperty("Description"));
        Assert.NotNull(typeof(PluginSkillDefinition).GetProperty("ContentPath"));
    }

    [Fact]
    public void List_WhenCalled_ReturnsUniquePluginAndConnectorIds()
    {
        IPluginCatalog catalog = new BuiltInPluginCatalog();

        var plugins = catalog.List();

        Assert.NotEmpty(plugins);
        Assert.Equal(
            plugins.Count,
            plugins.Select(plugin => plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );

        foreach (var plugin in plugins)
        {
            Assert.Equal(
                plugin.Connectors.Count,
                plugin.Connectors.Select(connector => connector.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            );
        }
    }

    [Fact]
    public void Find_ForGitHub_ReturnsOAuthPluginDefinition()
    {
        IPluginCatalog catalog = new BuiltInPluginCatalog();

        var plugin = Assert.IsType<PluginDefinition>(catalog.Find("github"));
        var connector = Assert.Single(plugin.Connectors);
        var authScheme = Assert.Single(connector.AuthSchemes);
        var capabilitySource = Assert.IsType<NativeCapabilitySourceDefinition>(
            Assert.Single(connector.CapabilitySources)
        );

        Assert.Equal("github", plugin.Id);
        Assert.Equal("github-cloud", connector.Id);
        Assert.Equal(AuthSchemeType.OAuth2, authScheme.Type);
        var oauth = Assert.IsType<OAuth2AuthorizationCodeSettings>(authScheme.OAuth2AuthorizationCode);
        Assert.True(oauth.UsePkce);
        Assert.Equal("https://github.com/login/oauth/authorize", oauth.AuthorizationEndpoint);
        Assert.Equal("https://github.com/login/oauth/access_token", oauth.TokenEndpoint);
        Assert.Equal("https://api.github.com/user", oauth.UserInfoEndpoint);
        Assert.Equal("client-id", oauth.ClientIdFieldId);
        Assert.Equal("client-secret", oauth.ClientSecretFieldId);
        Assert.Equal(OAuthSubjectSource.UserInfo, oauth.SubjectResolution.Source);
        Assert.Equal("login", oauth.SubjectResolution.Field);
        Assert.Equal(OAuth2ClientAuthenticationMethod.Body, oauth.ClientAuthenticationMethod);
        Assert.False(oauth.SupportsRefresh);
        Assert.Contains("repo", oauth.Scopes);
        Assert.Contains("client-secret", authScheme.InstallationFields.Select(field => field.Id));
        Assert.Equal(
            FormFieldType.Secret,
            authScheme.InstallationFields.Single(field => field.Id == "client-secret").Type
        );
        Assert.Equal("github", capabilitySource.Provider);
    }

    [Fact]
    public void PluginDefinition_WithMultipleConnectors_PreservesAllConnectors()
    {
        var plugin = new PluginDefinition
        {
            Id = "multi-service",
            Version = "1.0.0",
            DisplayName = "Multi Service",
            Connectors =
            [
                new ConnectorDefinition { Id = "primary", DisplayName = "Primary" },
                new ConnectorDefinition
                {
                    Id = "secondary",
                    DisplayName = "Secondary",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "access-key",
                            DisplayName = "Access key",
                            Type = AuthSchemeType.AkSk,
                            ConnectionFields =
                            [
                                new FormFieldDefinition
                                {
                                    Id = "access-key-id",
                                    Label = "Access key ID",
                                    Type = FormFieldType.Text,
                                    IsRequired = true,
                                },
                                new FormFieldDefinition
                                {
                                    Id = "secret-access-key",
                                    Label = "Secret access key",
                                    Type = FormFieldType.Secret,
                                    IsRequired = true,
                                },
                            ],
                        },
                    ],
                    CapabilitySources =
                    [
                        new McpCapabilitySourceDefinition
                        {
                            Id = "remote-mcp",
                            Transport = new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
                            CredentialBindings =
                            [
                                new CredentialBindingDefinition
                                {
                                    ValueSource = new ConnectionFieldCredentialValueSourceDefinition
                                    {
                                        AuthSchemeId = "access-key",
                                        FieldId = "access-key-id",
                                    },
                                    Target = CredentialBindingTarget.HttpHeader,
                                    TargetName = "X-Access-Key",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        Assert.Equal(["primary", "secondary"], plugin.Connectors.Select(connector => connector.Id));
        Assert.Equal(AuthSchemeType.AkSk, plugin.Connectors[1].AuthSchemes[0].Type);
        Assert.IsType<McpCapabilitySourceDefinition>(plugin.Connectors[1].CapabilitySources[0]);
    }

    [Fact]
    public void McpCapabilitySource_WithStdioTransport_DoesNotRequireEndpoint()
    {
        var source = new McpCapabilitySourceDefinition
        {
            Id = "local-mcp",
            Transport = new StdioMcpTransportDefinition { Command = "mcp-server", Arguments = ["--stdio"] },
        };

        var transport = Assert.IsType<StdioMcpTransportDefinition>(source.Transport);
        Assert.Equal("mcp-server", transport.Command);
        Assert.Equal(["--stdio"], transport.Arguments);
    }

    [Fact]
    public void Skills_WhenCatalogued_UseSafeExistingContentPaths()
    {
        IPluginCatalog catalog = new BuiltInPluginCatalog();
        var skills = catalog.List().SelectMany(plugin => plugin.Skills).ToList();
        var metadataReader = new PluginSkillMetadataReader(new AppContextPluginContentRootProvider());

        Assert.NotEmpty(skills);

        foreach (var skill in skills)
        {
            Assert.False(Path.IsPathRooted(skill.ContentPath));
            Assert.DoesNotContain(
                "..",
                skill.ContentPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );

            var fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, skill.ContentPath));
            Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), fullPath, StringComparison.Ordinal);
            Assert.True(File.Exists(fullPath), $"Expected plugin skill content at '{fullPath}'.");

            Assert.True(metadataReader.TryRead(skill, out var metadata));
            Assert.False(string.IsNullOrWhiteSpace(metadata.Id));
            Assert.False(string.IsNullOrWhiteSpace(metadata.Description));
        }
    }
}
