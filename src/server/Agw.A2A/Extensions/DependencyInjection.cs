using A2A;
using Agw.Agents.Execution.Durable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.A2A.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddA2A(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        var executionOptions =
            configuration.GetSection(ExecutionRuntimeOptions.SectionName).Get<ExecutionRuntimeOptions>()
            ?? new ExecutionRuntimeOptions();
        if (executionOptions.Provider == ExecutionProvider.Distributed)
        {
            services.AddSingleton<DurableA2AAgentExecutionBridge>();
            services.AddSingleton<IAgentExecutionBridge>(provider =>
                provider.GetRequiredService<DurableA2AAgentExecutionBridge>()
            );
            services.AddSingleton<IDurableA2AExecutionBridge>(provider =>
                provider.GetRequiredService<DurableA2AAgentExecutionBridge>()
            );
        }
        else
        {
            services.AddSingleton<IAgentExecutionBridge, A2AAgentExecutionBridge>();
        }
        services.AddSingleton(sp => new AgentHandlerFactory(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IAgentExecutionBridge>()
        ));
        services.AddSingleton<AgwChannelEventNotifier>();

        services.AddScoped<A2AAgentService>();
        services.AddScoped<ITaskStore, TaskStore>();
        services.AddScoped<IAgwA2ARequestHandler, AgwA2ARequestHandler>();
        services.Configure<AgwA2AServerOptions>(o => { });

        return services;
    }
}
