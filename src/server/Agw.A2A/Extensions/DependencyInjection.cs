using A2A;
using Agw.Agents.Contracts.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.A2A.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddA2A(this IServiceCollection services, IConfiguration configuration)
    {
        var supportsDurableOperations =
            Enum.TryParse<ExecutionProvider>(
                configuration[ExecutionRuntimeConfiguration.ProviderKey],
                ignoreCase: true,
                out var executionProvider
            )
            && executionProvider == ExecutionProvider.Distributed;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new A2AAgentExecutionBridge(
            provider.GetRequiredService<IServiceScopeFactory>(),
            supportsDurableOperations
        ));
        services.AddSingleton<IAgentExecutionBridge>(provider =>
            provider.GetRequiredService<A2AAgentExecutionBridge>()
        );
        services.AddSingleton<IDurableA2AExecutionBridge>(provider =>
            provider.GetRequiredService<A2AAgentExecutionBridge>()
        );
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
