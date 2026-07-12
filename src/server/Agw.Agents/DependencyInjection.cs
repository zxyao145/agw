using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Transport.SignalR;
using Agw.Agents.Execution.Turns;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Middleware;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AgentflowDomainService>();
        services.AddScoped<AgentflowAppService>();
        services.AddScoped<AgentflowTraceAppService>();
        services.AddScoped<AgentflowRuntimeService>();
        services.AddScoped<IAgentflowRuntimeService, AgentflowRuntimeService>();
        services.AddScoped<McpToolServerDomainService>();
        services.AddScoped<AgentDomainService>();
        services.AddScoped<AgentAppService>();
        services.AddScoped<McpToolServerAppService>();
        services.AddScoped<AgentSessionStateStore>();
        services.AddScoped<IAgentRuntimeService, AgentRuntimeService>();
        services.AddScoped<IRuntimeFactory, RuntimeFactory>();
        services.AddScoped<IExecutionCommandHandler, SettingCommandHandler>();
        services.AddScoped<IExecutionCommandHandler, ExecCommandHandler>();
        services.AddScoped<IExecutionCommandHandler, InterruptCommandHandler>();
        services.AddScoped<IExecutionCommandHandler, HumanResponseCommandHandler>();
        services.AddScoped<ExecutionCommandDispatcher>();
        services.AddSingleton<ExecutionConnectionRegistry>();
        services.AddSingleton<RuntimeTurnContextAccessor>();
        services.AddSingleton<IRuntimeTurnContextAccessor, RuntimeTurnContextAccessor>();
        services.AddSingleton<ObservabilityMiddleware>();
        services.AddSingleton<UsageTrackingMiddleware>();
        services.AddSingleton<IAgentflowNodeExecutionTraceStore, AgentflowNodeExecutionTraceStore>();
        services.AddSingleton<AgentflowNodeExecutionTraceCollector>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<AgentflowNodeExecutionTraceCollector>());

        return services;
    }
}
