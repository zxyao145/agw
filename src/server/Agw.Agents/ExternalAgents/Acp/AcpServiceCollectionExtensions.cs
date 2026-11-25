using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Infrastructure.Acp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Agents.ExternalAgents.Acp;

public static class AcpServiceCollectionExtensions
{
    public static IServiceCollection AddAcpAgentInstallation(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AcpAgentOptions>()
            .Bind(configuration.GetSection(AcpAgentOptions.SectionName))
            .Validate(
                options =>
                    options.RegistryUrl is not null && AcpRegistryRules.IsAllowedDownloadUri(options.RegistryUrl),
                "AcpAgents:RegistryUrl must be an HTTPS URL or a loopback HTTP URL."
            )
            .Validate(
                options => string.IsNullOrEmpty(options.InstallRoot) || Path.IsPathFullyQualified(options.InstallRoot),
                "AcpAgents:InstallRoot must be an absolute path."
            )
            .Validate(
                options =>
                    options.RegistryRefreshInterval > TimeSpan.Zero
                    && options.RegistryFetchTimeout > TimeSpan.Zero
                    && options.RegistryMaxBytes > 0
                    && options.DownloadTimeout > TimeSpan.Zero
                    && options.MaxDownloadBytes > 0
                    && options.InitializeTimeout > TimeSpan.Zero
                    && options.RequestTimeout > TimeSpan.Zero
                    && options.CancelGracePeriod > TimeSpan.Zero
                    && options.StderrTailLines > 0,
                "ACP timeouts, intervals and limits must be greater than zero."
            )
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services
            .AddHttpClient(AcpDownloadClient.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<AcpDownloadClient>();
        services.TryAddSingleton<IAcpRegistryClient, AcpRegistryClient>();
        services.TryAddSingleton<IAcpInstalledAgentCatalog, AcpInstalledAgentCatalog>();
        services.TryAddSingleton<IAcpAgentInstaller, AcpAgentInstaller>();
        return services;
    }
}
