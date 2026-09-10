using Agw.Tools.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Tools;

public static class DependencyInjection
{
    public static IServiceCollection AddTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<UserMemoryAppService>();
        services.AddSingleton(serviceProvider =>
        {
            var registry = new ToolRegistryService(
                serviceProvider.GetRequiredService<ILogger<ToolRegistryService>>(),
                serviceProvider,
                discoverAllToolKinds: true
            );
            registry.ValidateDefinitionCoverage();
            return registry;
        });
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<ToolRegistryService>().ToolBlocks);
        return services;
    }
}
