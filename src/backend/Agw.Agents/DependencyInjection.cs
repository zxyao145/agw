using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.Agents;
using Agw.Agents.Application.Execution;
using Agw.Agents.Application.Execution.CommandStrategies;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AgentflowDomainService>();
        services.AddScoped<AgentflowRuntimeService>();
        services.AddScoped<McpToolServerDomainService>();
        services.AddScoped<AgentDomainService>();
        services.AddScoped<AgentAppService>();
        services.AddScoped<McpToolServerAppService>();
        services.AddScoped<AgentSessionStateStore>();
        services.AddScoped<IAgentRuntimeService, AgentRuntimeService>();
        services.AddScoped<IExecutionCommandStrategy, SettingCommandStrategy>();
        services.AddScoped<IExecutionCommandStrategy, ExecCommandStrategy>();
        services.AddScoped<IExecutionCommandStrategy, HumanResponseCommandStrategy>();
        services.AddScoped<IExecutionCommandStrategy, InterruptCommandStrategy>();
        services.AddScoped<CommandDispatcher>();
        services.AddSingleton<LoggingMiddleware>();
        
        return services;
    }
}
