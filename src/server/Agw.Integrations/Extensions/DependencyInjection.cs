using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Capabilities;
using Agw.Integrations.Infrastructure.Plugins;
using Agw.Integrations.Mcp;
using Agw.Integrations.Tools.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Integrations.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection();
        services
            .AddOptions<OAuthRedirectOptions>()
            .Bind(configuration.GetSection(OAuthRedirectOptions.SectionName))
            .Validate(
                options => OAuthRedirectUriResolver.IsValidOptionalBaseUrl(options.PublicBaseUrl),
                "Integrations:OAuth:PublicBaseUrl must be an absolute HTTP(S) base URL."
            )
            .Validate(
                options => OAuthRedirectUriResolver.IsValidOptionalBaseUrl(options.WebBaseUrl),
                "Integrations:OAuth:WebBaseUrl must be an absolute HTTP(S) base URL."
            )
            .ValidateOnStart();
        services.AddSingleton<IPluginCatalog, BuiltInPluginCatalog>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IConnectionCredentialReader, ConnectionCredentialReader>();
        services.AddScoped<CredentialMutationService>();
        services.AddScoped<PluginCatalogAppService>();
        services.AddScoped<PluginInstallationAppService>();
        services.AddScoped<ConnectionAppService>();
        services.AddSingleton<OAuthStateProtector>();
        services.AddSingleton<OAuthRedirectUriResolver>();
        services.AddScoped<OAuthAuthorizationAppService>();
        services.AddScoped<OAuthRefreshAppService>();
        services.AddSingleton<IMcpToolMaterializer, McpToolMaterializer>();
        services.AddSingleton<IConnectionMcpToolInvoker, ScopedConnectionMcpToolInvoker>();
        services.AddSingleton<IPluginContentRootProvider, AppContextPluginContentRootProvider>();
        services.AddSingleton<PluginSkillMetadataReader>();
        services.AddSingleton<IConnectionNativeCapabilityProvider, GitHubConnectionNativeCapabilityProvider>();
        services.AddScoped<IConnectionCapabilityResolver, ConnectionCapabilityResolver>();
        services.AddScoped<IConnectionMcpInvocationSession>(provider =>
            (ConnectionCapabilityResolver)provider.GetRequiredService<IConnectionCapabilityResolver>()
        );
        services.AddScoped<IProjectWorkspaceResolver, ProjectWorkspaceResolver>();
        services.AddScoped<IGitHubConnectionInvoker, GitHubConnectionInvoker>();

        return services;
    }
}
