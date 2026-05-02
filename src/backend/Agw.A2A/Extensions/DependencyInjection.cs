using A2A;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddA2A(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentExecutionBridge, AgentExecutionBridge>();
        services.AddSingleton(sp => new AgentHandlerFactory(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IAgentExecutionBridge>()));
        services.AddSingleton<AgwChannelEventNotifier>();

        services.AddScoped<A2AAgentService>();
        services.AddScoped<ITaskStore, TaskStore>();
        services.AddScoped<IAgwA2ARequestHandler, AgwA2ARequestHandler>();
        services.Configure<AgwA2AServerOptions>(o =>
        {
        });

        return services;
    }
}
