using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Infrastructure.Plugins;

/// <summary>
/// 所有可以使用的 plugin 列表
/// </summary>
public sealed class BuiltInPluginCatalog : IPluginCatalog
{
    private static readonly IReadOnlyList<PluginDefinition> Plugins =
    [
        new PluginDefinition
        {
            Id = "github",
            Version = "1.0.0",
            DisplayName = "GitHub",
            Description = "Connect GitHub accounts and use repository capabilities.",
            Tags = ["Git", "Coding"],
            Connectors =
            [
                new ConnectorDefinition
                {
                    Id = "github-cloud",
                    DisplayName = "GitHub Cloud",
                    Description = "Connect a GitHub.com account using OAuth.",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "oauth2",
                            DisplayName = "OAuth 2.0",
                            Type = AuthSchemeType.OAuth2,
                            OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                            {
                                AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                                TokenEndpoint = "https://github.com/login/oauth/access_token",
                                UserInfoEndpoint = "https://api.github.com/user",
                                ClientIdFieldId = "client-id",
                                ClientSecretFieldId = "client-secret",
                                SubjectResolution = new OAuthSubjectResolutionDefinition
                                {
                                    Source = OAuthSubjectSource.UserInfo,
                                    Field = "login",
                                },
                                UsePkce = true,
                                ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.Body,
                                SupportsRefresh = false,
                                Scopes = ["repo", "read:user", "read:org"],
                            },
                            InstallationFields =
                            [
                                new FormFieldDefinition
                                {
                                    Id = "client-id",
                                    Label = "Client ID",
                                    Type = FormFieldType.Text,
                                    IsRequired = true,
                                },
                                new FormFieldDefinition
                                {
                                    Id = "client-secret",
                                    Label = "Client secret",
                                    Type = FormFieldType.Secret,
                                    IsRequired = true,
                                },
                            ],
                        },
                    ],
                    CapabilitySources =
                    [
                        // Agw 内部 C# Provider 创建工具。
                        new NativeCapabilitySourceDefinition { Id = "github-native", Provider = "github" },
                    ],
                },
            ],
            Skills = [new PluginSkillDefinition { ContentPath = "Plugins/github/skills/github/SKILL.md" }],
        },
    ];

    public BuiltInPluginCatalog()
    {
        PluginCatalogValidator.Validate(Plugins);
    }

    public IReadOnlyList<PluginDefinition> List()
    {
        return Plugins;
    }

    public PluginDefinition? Find(string pluginId)
    {
        return Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }
}
