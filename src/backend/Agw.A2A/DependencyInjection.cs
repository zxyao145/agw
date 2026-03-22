using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A;

public static class DependencyInjection
{
    public static IServiceCollection AddA2A(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<A2AAgentService>();
        services.Configure<A2AServerOptions>(o =>
        {

        });

        services.AddSingleton<TaskManagerFactory>(sp =>
        {
            return new TaskManagerFactory(sp);
        });

        return services;
    }
}
