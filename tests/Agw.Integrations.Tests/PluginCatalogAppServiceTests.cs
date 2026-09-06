using Agw.Infrastructure.Data;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Infrastructure.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tests;

public class PluginCatalogAppServiceTests
{
    [Fact]
    public void List_WhenGitHubIsBuiltIn_ReturnsExplicitCompleteMetadata()
    {
        using var dbContext = new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("Data Source=:memory:").Options
        );
        var service = new PluginCatalogAppService(
            new BuiltInPluginCatalog(),
            dbContext,
            new PluginSkillMetadataReader(new AppContextPluginContentRootProvider()),
            new TestUserInfoService()
        );

        var plugins = service.List();

        var plugin = Assert.Single(plugins);
        Assert.IsType<PluginResponse>(plugin);
        Assert.Equal("github", plugin.Id);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(["Git", "Coding"], plugin.Tags);

        var connector = Assert.Single(plugin.Connectors);
        Assert.Equal("github-cloud", connector.Id);

        var authScheme = Assert.Single(connector.AuthSchemes);
        Assert.Equal("oauth2", authScheme.Id);
        Assert.Equal(AuthSchemeTypeResponse.OAuth2, authScheme.Type);
        Assert.Equal(["client-id", "client-secret"], authScheme.InstallationFields.Select(field => field.Id));
        Assert.Empty(authScheme.ConnectionFields);

        var oauth = Assert.IsType<OAuth2AuthorizationCodeResponse>(authScheme.OAuth2AuthorizationCode);
        Assert.Equal("https://github.com/login/oauth/authorize", oauth.AuthorizationEndpoint);
        Assert.Equal("https://github.com/login/oauth/access_token", oauth.TokenEndpoint);
        Assert.Equal("https://api.github.com/user", oauth.UserInfoEndpoint);
        Assert.Equal("client-id", oauth.ClientIdFieldId);
        Assert.Equal("client-secret", oauth.ClientSecretFieldId);
        Assert.Equal(OAuthSubjectSourceResponse.UserInfo, oauth.SubjectResolution.Source);
        Assert.Equal("login", oauth.SubjectResolution.Field);
        Assert.True(oauth.UsePkce);
        Assert.False(oauth.SupportsRefresh);
        Assert.Equal(["repo", "read:user", "read:org"], oauth.Scopes);

        var source = Assert.Single(connector.CapabilitySources);
        Assert.Equal(CapabilitySourceKindResponse.Native, source.Kind);
        Assert.Equal("github-native", source.Id);
        Assert.Equal("github", source.Provider);
        Assert.Null(source.McpTransport);
        Assert.Empty(source.CredentialBindings);

        var skill = Assert.Single(plugin.Skills);
        Assert.Equal("github", skill.Id);
        Assert.Equal("Use connected GitHub tools to inspect and work with repositories.", skill.Description);
        Assert.EndsWith("SKILL.md", skill.ContentPath, StringComparison.Ordinal);
    }

    [Fact]
    public void List_WhenCalled_DoesNotExposeConfigurationValues()
    {
        var responseProperties = GetResponseTypes(typeof(PluginResponse))
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Value", responseProperties);
        Assert.DoesNotContain("SecretValue", responseProperties);
        Assert.DoesNotContain("ProtectedValue", responseProperties);
        Assert.DoesNotContain("EnvironmentVariableName", responseProperties);
    }

    private static IReadOnlyCollection<Type> GetResponseTypes(Type root)
    {
        var result = new HashSet<Type>();
        var pending = new Queue<Type>();
        pending.Enqueue(root);

        while (pending.TryDequeue(out var type))
        {
            if (!result.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties())
            {
                var propertyType = property.PropertyType;
                if (propertyType.IsArray)
                {
                    propertyType = propertyType.GetElementType()!;
                }
                else if (propertyType.IsGenericType)
                {
                    propertyType = propertyType.GetGenericArguments().Last();
                }

                if (propertyType.Namespace == typeof(PluginResponse).Namespace)
                {
                    pending.Enqueue(propertyType);
                }
            }
        }

        return result;
    }
}
