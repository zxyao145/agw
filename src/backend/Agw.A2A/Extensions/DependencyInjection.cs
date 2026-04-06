using A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddA2A(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<A2AAgentService>();
        services.AddScoped<ITaskStore, TaskStore>();
        services.Configure<A2AServerOptions>(o =>
        {
        });


        return services;
    }
}
