using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Facades;
using Agw.Agents.Definitions.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents;

/// <summary>
/// 注册 Agents 模块的定义管理与目录查询。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AgentflowAppService>();
        services.AddScoped<AgentflowTraceAppService>();
        services.AddScoped<IAgentflowDefinitionReader, AgentflowDefinitionReader>();
        services.AddScoped<AgentAppService>();
        services.AddScoped<AgentCatalogFacade>();
        services.AddScoped<IAgentCatalogFacade>(provider => provider.GetRequiredService<AgentCatalogFacade>());
        services.AddScoped<IAgentReferenceFacade>(provider => provider.GetRequiredService<AgentCatalogFacade>());
        services.AddScoped<AgentSuggestionAppService>();
        services.AddScoped<ExecutionPermissionService>();
        services.AddScoped<McpToolServerAppService>();
        return services;
    }
}
