using Agw.Tools.Abstractions.Generated;
using Agw.Tools.Application;
using Agw.Tools.Domain.Repositories;
using Agw.Tools.Domain.Services;
using Agw.Tools.Generated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Tools;

public static class DependencyInjection
{
    public static IServiceCollection AddToolCatalogTypes(this IServiceCollection services, params Type[] toolTypes)
    {
        ArgumentNullException.ThrowIfNull(toolTypes);
        foreach (Type toolType in toolTypes)
        {
            ArgumentNullException.ThrowIfNull(toolType);
            services.AddSingleton(new AgwToolCatalogType(toolType));
        }
        return services;
    }

    public static IServiceCollection AddTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IUserMemoryRepository, UserMemoryRepository>();
        services.AddScoped<UserMemoryNameUniquenessDomainService>();
        services.AddScoped<UserMemoryAppService>();
        services.AddScoped<AgwToolInvocationContext>();
        services.AddScoped<IAgwToolInvocationContext>(static serviceProvider =>
            serviceProvider.GetRequiredService<AgwToolInvocationContext>()
        );
        services.AddScoped<IAgwToolInvocationContextInitializer>(static serviceProvider =>
            serviceProvider.GetRequiredService<AgwToolInvocationContext>()
        );
        services.AddSingleton<IAgwGeneratedToolModule>(Agw.Generated.Agw.Tools.AgwToolModule.Instance);
        services.AddSingleton<AgwGeneratedToolCatalog>();
        services.AddSingleton(serviceProvider =>
        {
            var registry = new ToolRegistryService(
                serviceProvider.GetRequiredService<ILogger<ToolRegistryService>>(),
                serviceProvider,
                generatedToolCatalog: serviceProvider.GetRequiredService<AgwGeneratedToolCatalog>(),
                generatedToolTypes: serviceProvider
                    .GetServices<AgwToolCatalogType>()
                    .Select(static selection => selection.Type),
                discoverAllToolKinds: true
            );
            registry.ValidateDefinitionCoverage();
            return registry;
        });
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<ToolRegistryService>().ToolBlocks);
        return services;
    }
}
