using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Tests;

public class PluginCatalogValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("invalid id")]
    [InlineData("-invalid")]
    public void Validate_PluginIdIsInvalid_Throws(string pluginId)
    {
        var plugins = new[] { CreatePlugin(id: pluginId) };

        Assert.Throws<InvalidOperationException>(() => PluginCatalogValidator.Validate(plugins));
    }

    [Fact]
    public void Validate_PluginIdsDifferOnlyByCase_Throws()
    {
        var plugins = new[] { CreatePlugin(id: "github"), CreatePlugin(id: "GitHub") };

        Assert.Throws<InvalidOperationException>(() => PluginCatalogValidator.Validate(plugins));
    }

    [Fact]
    public void Validate_ConnectorIdsDifferOnlyByCase_Throws()
    {
        var plugin = CreatePlugin(connectors: [CreateConnector(id: "cloud"), CreateConnector(id: "Cloud")]);

        Assert.Throws<InvalidOperationException>(() => PluginCatalogValidator.Validate([plugin]));
    }

    [Fact]
    public void Validate_AuthSchemeIdsDifferOnlyByCase_Throws()
    {
        var connector = CreateConnector(authSchemes: [CreateApiKeyAuth("api-key"), CreateApiKeyAuth("API-Key")]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_CapabilitySourceIdsDifferOnlyByCase_Throws()
    {
        var connector = CreateConnector(
            capabilitySources: [CreateNativeSource("native"), CreateNativeSource("Native")]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_FieldIdsDifferOnlyByCaseWithinAuthScheme_Throws()
    {
        var authScheme = CreateApiKeyAuth("api-key", [CreateField("token"), CreateField("Token")]);
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_OAuthSchemeHasNoTypedSettings_Throws()
    {
        var authScheme = new AuthSchemeDefinition
        {
            Id = "oauth2",
            DisplayName = "OAuth 2.0",
            Type = AuthSchemeType.OAuth2,
        };
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_OAuthSettingsHaveNoUserInfoEndpoint_Throws()
    {
        var authScheme = CreateOAuthAuth(userInfoEndpoint: "");
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Theory]
    [InlineData("client_id")]
    [InlineData("response_type")]
    [InlineData("redirect_uri")]
    [InlineData("state")]
    [InlineData("scope")]
    [InlineData("code_challenge")]
    [InlineData("code_challenge_method")]
    public void Validate_OAuthAdditionalAuthorizeParameterIsReserved_Throws(string parameterName)
    {
        var authScheme = CreateOAuthAuth(
            userInfoEndpoint: "https://example.test/userinfo",
            additionalAuthorizeParameters: new Dictionary<string, string> { [parameterName] = "plugin-controlled" }
        );
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_StdioMcpTransportHasNoCommand_Throws()
    {
        var source = CreateMcpSource(new StdioMcpTransportDefinition { Command = "" });
        var connector = CreateConnector(capabilitySources: [source]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_HttpMcpTransportHasNoEndpoint_Throws()
    {
        var source = CreateMcpSource(new HttpMcpTransportDefinition { Endpoint = "" });
        var connector = CreateConnector(capabilitySources: [source]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_SseMcpTransportHasNoEndpoint_Throws()
    {
        var source = CreateMcpSource(new SseMcpTransportDefinition { Endpoint = "" });
        var connector = CreateConnector(capabilitySources: [source]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_CredentialBoundHttpMcpEndpointIsNotTls_Throws(bool useSse)
    {
        McpTransportDefinition transport = useSse
            ? new SseMcpTransportDefinition { Endpoint = "http://example.test/mcp" }
            : new HttpMcpTransportDefinition { Endpoint = "http://example.test/mcp" };
        var source = CreateMcpSource(transport, [CreateConnectionFieldBinding(CredentialBindingTarget.HttpHeader)]);
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_CredentialBindingReferencesUnknownField_Throws()
    {
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = new ConnectionFieldCredentialValueSourceDefinition
                    {
                        AuthSchemeId = "api-key",
                        FieldId = "missing-token",
                    },
                    Target = CredentialBindingTarget.HttpHeader,
                    TargetName = "Authorization",
                },
            ]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_CredentialBindingReferencesFieldFromAnotherAuthScheme_Throws()
    {
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = new ConnectionFieldCredentialValueSourceDefinition
                    {
                        AuthSchemeId = "first",
                        FieldId = "second-token",
                    },
                    Target = CredentialBindingTarget.HttpHeader,
                    TargetName = "Authorization",
                },
            ]
        );
        var connector = CreateConnector(
            authSchemes:
            [
                CreateApiKeyAuth("first", [CreateField("first-token")]),
                CreateApiKeyAuth("second", [CreateField("second-token")]),
            ],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_StdioBindingTargetsHttpHeader_Throws()
    {
        var source = CreateMcpSource(
            new StdioMcpTransportDefinition { Command = "mcp-server" },
            [CreateConnectionFieldBinding(CredentialBindingTarget.HttpHeader)]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_HttpOrSseBindingTargetsEnvironmentVariable_Throws(bool useSse)
    {
        McpTransportDefinition transport = useSse
            ? new SseMcpTransportDefinition { Endpoint = "https://example.test/mcp" }
            : new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" };
        var source = CreateMcpSource(
            transport,
            [CreateConnectionFieldBinding(CredentialBindingTarget.EnvironmentVariable)]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_OAuthAccessTokenBindingForNonOAuthScheme_Throws()
    {
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = new OAuthAccessTokenCredentialValueSourceDefinition { AuthSchemeId = "api-key" },
                    Target = CredentialBindingTarget.HttpHeader,
                    TargetName = "Authorization",
                    ValuePrefix = "Bearer ",
                },
            ]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_CredentialBindingPrefixContainsNewLine_Throws()
    {
        var binding = CreateConnectionFieldBinding(CredentialBindingTarget.HttpHeader);
        binding = new CredentialBindingDefinition
        {
            ValueSource = binding.ValueSource,
            Target = binding.Target,
            TargetName = binding.TargetName,
            ValuePrefix = "Bearer\r\nInjected: ",
        };
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [binding]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_CredentialBindingsTargetSameHeaderIgnoringCase_Throws()
    {
        var first = CreateConnectionFieldBinding(CredentialBindingTarget.HttpHeader);
        var second = new CredentialBindingDefinition
        {
            ValueSource = first.ValueSource,
            Target = CredentialBindingTarget.HttpHeader,
            TargetName = "authorization",
        };
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [first, second]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Bad\r\nHeader")]
    public void Validate_HttpHeaderTargetNameIsInvalid_Throws(string targetName)
    {
        var binding = CreateConnectionFieldBinding(CredentialBindingTarget.HttpHeader);
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = binding.ValueSource,
                    Target = binding.Target,
                    TargetName = targetName,
                },
            ]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Theory]
    [InlineData("BAD=NAME")]
    [InlineData("BAD\0NAME")]
    public void Validate_EnvironmentVariableTargetNameIsInvalid_Throws(string targetName)
    {
        var binding = CreateConnectionFieldBinding(CredentialBindingTarget.EnvironmentVariable);
        var source = CreateMcpSource(
            new StdioMcpTransportDefinition { Command = "mcp-server" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = binding.ValueSource,
                    Target = binding.Target,
                    TargetName = targetName,
                },
            ]
        );
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources: [source]
        );

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_OAuthClientSecretFieldIsNotSecret_Throws()
    {
        var authScheme = CreateOAuthAuth(
            userInfoEndpoint: "https://example.test/user",
            installationFields:
            [
                CreateField("client-id", FormFieldType.Text),
                CreateField("client-secret", FormFieldType.Text),
            ]
        );
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_OAuthClientIdFieldDoesNotExist_Throws()
    {
        var authScheme = CreateOAuthAuth(
            userInfoEndpoint: "https://example.test/user",
            installationFields: [CreateField("client-secret")],
            clientIdFieldId: "missing-client-id"
        );
        var connector = CreateConnector(authSchemes: [authScheme]);

        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])])
        );
    }

    [Fact]
    public void Validate_TokenResponseSubjectWithoutUserInfoEndpoint_DoesNotThrow()
    {
        var authScheme = CreateOAuthAuth(userInfoEndpoint: null, subjectSource: OAuthSubjectSource.TokenResponse);
        var connector = CreateConnector(authSchemes: [authScheme]);

        PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])]);
    }

    [Fact]
    public void Validate_InstallationFieldBindingForMatchingAuthScheme_DoesNotThrow()
    {
        var authScheme = new AuthSchemeDefinition
        {
            Id = "api-key",
            DisplayName = "API key",
            Type = AuthSchemeType.ApiKey,
            InstallationFields = [CreateField("shared-token")],
        };
        var source = CreateMcpSource(
            new StdioMcpTransportDefinition { Command = "mcp-server" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = new InstallationFieldCredentialValueSourceDefinition
                    {
                        AuthSchemeId = "api-key",
                        FieldId = "shared-token",
                    },
                    Target = CredentialBindingTarget.EnvironmentVariable,
                    TargetName = "API_TOKEN",
                },
            ]
        );
        var connector = CreateConnector(authSchemes: [authScheme], capabilitySources: [source]);

        PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])]);
    }

    [Fact]
    public void Validate_OAuthAccessTokenBindingWithBearerPrefix_DoesNotThrow()
    {
        var authScheme = CreateOAuthAuth("https://example.test/user");
        var source = CreateMcpSource(
            new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
            [
                new CredentialBindingDefinition
                {
                    ValueSource = new OAuthAccessTokenCredentialValueSourceDefinition { AuthSchemeId = "oauth2" },
                    Target = CredentialBindingTarget.HttpHeader,
                    TargetName = "Authorization",
                    ValuePrefix = "Bearer ",
                },
            ]
        );
        var connector = CreateConnector(authSchemes: [authScheme], capabilitySources: [source]);

        PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])]);
    }

    [Fact]
    public void Validate_CoreDisplayValuesAreEmpty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PluginCatalogValidator.Validate([CreatePlugin(version: "")]));
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(displayName: "")])
        );
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [CreateConnector(displayName: "")])])
        );
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([
                CreatePlugin(
                    connectors: [CreateConnector(authSchemes: [CreateApiKeyAuth("api-key", displayName: "")])]
                ),
            ])
        );
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([
                CreatePlugin(
                    connectors:
                    [
                        CreateConnector(authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token", label: "")])]),
                    ]
                ),
            ])
        );
    }

    [Fact]
    public void Validate_InvalidEnumValues_Throws()
    {
        var invalidAuth = new AuthSchemeDefinition
        {
            Id = "auth",
            DisplayName = "Auth",
            Type = (AuthSchemeType)999,
        };
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([CreatePlugin(connectors: [CreateConnector(authSchemes: [invalidAuth])])])
        );

        var invalidField = CreateField("token", (FormFieldType)999);
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([
                CreatePlugin(connectors: [CreateConnector(authSchemes: [CreateApiKeyAuth("api-key", [invalidField])])]),
            ])
        );

        var invalidSubject = CreateOAuthAuth("https://example.test/user", subjectSource: (OAuthSubjectSource)999);
        Assert.Throws<InvalidOperationException>(() =>
            PluginCatalogValidator.Validate([
                CreatePlugin(connectors: [CreateConnector(authSchemes: [invalidSubject])]),
            ])
        );
    }

    [Theory]
    [InlineData("/absolute/SKILL.md")]
    [InlineData("skills/../secret/SKILL.md")]
    public void Validate_SkillPathIsUnsafe_Throws(string contentPath)
    {
        var plugin = CreatePlugin(skills: [CreateSkill(contentPath)]);

        Assert.Throws<InvalidOperationException>(() => PluginCatalogValidator.Validate([plugin]));
    }

    [Fact]
    public void Validate_ValidNonOAuthAndMcpDefinition_DoesNotThrow()
    {
        var connector = CreateConnector(
            authSchemes: [CreateApiKeyAuth("api-key", [CreateField("token")])],
            capabilitySources:
            [
                CreateMcpSource(
                    new HttpMcpTransportDefinition { Endpoint = "https://example.test/mcp" },
                    [
                        new CredentialBindingDefinition
                        {
                            ValueSource = new ConnectionFieldCredentialValueSourceDefinition
                            {
                                AuthSchemeId = "api-key",
                                FieldId = "token",
                            },
                            Target = CredentialBindingTarget.HttpHeader,
                            TargetName = "Authorization",
                        },
                    ]
                ),
            ]
        );

        PluginCatalogValidator.Validate([CreatePlugin(connectors: [connector])]);
    }

    private static PluginDefinition CreatePlugin(
        string id = "plugin",
        IReadOnlyList<ConnectorDefinition>? connectors = null,
        IReadOnlyList<PluginSkillDefinition>? skills = null,
        string version = "1.0.0",
        string displayName = "Plugin"
    )
    {
        return new PluginDefinition
        {
            Id = id,
            Version = version,
            DisplayName = displayName,
            Connectors = connectors ?? [CreateConnector()],
            Skills = skills ?? [],
        };
    }

    private static ConnectorDefinition CreateConnector(
        string id = "connector",
        IReadOnlyList<AuthSchemeDefinition>? authSchemes = null,
        IReadOnlyList<CapabilitySourceDefinition>? capabilitySources = null,
        string displayName = "Connector"
    )
    {
        return new ConnectorDefinition
        {
            Id = id,
            DisplayName = displayName,
            AuthSchemes = authSchemes ?? [],
            CapabilitySources = capabilitySources ?? [],
        };
    }

    private static AuthSchemeDefinition CreateApiKeyAuth(
        string id,
        IReadOnlyList<FormFieldDefinition>? connectionFields = null,
        string displayName = "API key"
    )
    {
        return new AuthSchemeDefinition
        {
            Id = id,
            DisplayName = displayName,
            Type = AuthSchemeType.ApiKey,
            ConnectionFields = connectionFields ?? [],
        };
    }

    private static AuthSchemeDefinition CreateOAuthAuth(
        string? userInfoEndpoint,
        IReadOnlyList<FormFieldDefinition>? installationFields = null,
        string clientIdFieldId = "client-id",
        OAuthSubjectSource subjectSource = OAuthSubjectSource.UserInfo,
        IReadOnlyDictionary<string, string>? additionalAuthorizeParameters = null
    )
    {
        return new AuthSchemeDefinition
        {
            Id = "oauth2",
            DisplayName = "OAuth 2.0",
            Type = AuthSchemeType.OAuth2,
            OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
            {
                AuthorizationEndpoint = "https://example.test/authorize",
                TokenEndpoint = "https://example.test/token",
                UserInfoEndpoint = userInfoEndpoint,
                ClientIdFieldId = clientIdFieldId,
                ClientSecretFieldId = "client-secret",
                SubjectResolution = new OAuthSubjectResolutionDefinition { Source = subjectSource, Field = "id" },
                ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.Body,
                AdditionalAuthorizeParameters = additionalAuthorizeParameters ?? new Dictionary<string, string>(),
            },
            InstallationFields =
                installationFields ?? [CreateField("client-id", FormFieldType.Text), CreateField("client-secret")],
        };
    }

    private static FormFieldDefinition CreateField(
        string id,
        FormFieldType type = FormFieldType.Secret,
        string label = "Token"
    )
    {
        return new FormFieldDefinition
        {
            Id = id,
            Label = label,
            Type = type,
        };
    }

    private static NativeCapabilitySourceDefinition CreateNativeSource(string id)
    {
        return new NativeCapabilitySourceDefinition { Id = id, Provider = "provider" };
    }

    private static McpCapabilitySourceDefinition CreateMcpSource(
        McpTransportDefinition transport,
        IReadOnlyList<CredentialBindingDefinition>? credentialBindings = null
    )
    {
        return new McpCapabilitySourceDefinition
        {
            Id = "mcp",
            Transport = transport,
            CredentialBindings = credentialBindings ?? [],
        };
    }

    private static PluginSkillDefinition CreateSkill(string contentPath)
    {
        return new PluginSkillDefinition { ContentPath = contentPath };
    }

    private static CredentialBindingDefinition CreateConnectionFieldBinding(CredentialBindingTarget target)
    {
        return new CredentialBindingDefinition
        {
            ValueSource = new ConnectionFieldCredentialValueSourceDefinition
            {
                AuthSchemeId = "api-key",
                FieldId = "token",
            },
            Target = target,
            TargetName = target == CredentialBindingTarget.HttpHeader ? "Authorization" : "API_TOKEN",
        };
    }
}
