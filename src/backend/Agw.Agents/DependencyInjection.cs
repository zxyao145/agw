using Agw.Agents.ExternalAgents;
using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Services.Agentflows;
using Agw.Domain.Services.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ClaudeCodeService>();
        services.AddScoped<AgentflowDomainService>();
        services.AddScoped<AgentflowRuntimeService>();
        services.AddScoped<McpToolServerDomainService>();
        services.AddScoped<AgentDomainService>();
        services.AddScoped<AgentRuntimeService>();

        return services;
    }
}
