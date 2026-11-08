using Agw.Extensions;
using Agw.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agw.Agents.Tests;

internal static class ExtensionRuntimeTestServices
{
    public static IServiceCollection AddTestExtensions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHostEnvironment>(
            Microsoft
                .Extensions.Hosting.Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings
                    {
                        ContentRootPath = AppContext.BaseDirectory,
                        DisableDefaults = true,
                    }
                )
                .Environment
        );
        return services
            .AddExtensions(configuration, "Standalone")
            .AddExtensionCapabilities()
            .AddExtensionInfrastructure();
    }
}
