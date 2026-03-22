using Agw.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools;

public static class DependencyInjection
{
    public static IServiceCollection AddTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ToolRegistryService>(); // Singleton to cache tool discovery
        return services;
    }
}
