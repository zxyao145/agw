using Microsoft.Extensions.DependencyInjection;

namespace Agw.Integrations.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }
}
