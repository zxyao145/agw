using Agw.Agents.Application.A2A;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Facades;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Agents.Infrastructure.A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agw.Agents;

/// <summary>
/// 注册 Agents 模块的定义管理与目录查询。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services
            .AddOptions<A2ARemoteAgentOptions>()
            .Bind(configuration.GetSection(A2ARemoteAgentOptions.SectionName))
            .Validate(options =>
                options.CardMaxBytes > 0
                && options.CardFetchTimeout > TimeSpan.Zero
                && options.RequestTimeout > TimeSpan.Zero
                && options.StreamIdleTimeout > TimeSpan.Zero
                && options.PollInterval > TimeSpan.Zero
                && options.ResubscribeAttempts >= 0
            )
            .ValidateOnStart();
        services
            .AddHttpClient(A2ARemoteHttpClientFactory.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(() => new A2AOriginCredentialHandler())
            .ConfigurePrimaryHttpMessageHandler(provider =>
                A2ARemoteHttpClientFactory.CreateTransport(
                    provider.GetRequiredService<IOptions<A2ARemoteAgentOptions>>().Value.AllowPrivateNetworks
                )
            )
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        services.AddSingleton<IA2ARemoteHttpClientFactory, A2ARemoteHttpClientFactory>();
        services.AddSingleton<IA2AAgentCardClient, A2AAgentCardClient>();
        services.AddScoped<IAgentDefinitionRepository, AgentDefinitionRepository>();
        services.AddScoped<AgentDefinitionDomainService>();
        services.AddScoped<AgentResourceBindingDomainService>();
        services.AddScoped<AgentflowDefinitionDomainService>();
        services.AddScoped<AgentflowAppService>();
        services.AddScoped<AgentflowTraceAppService>();
        services.AddScoped<IAgentflowDefinitionReader, AgentflowDefinitionReader>();
        services.AddScoped<IAgentflowRevisionSnapshotReader, AgentflowRevisionSnapshotReader>();
        services.AddScoped<AgentAppService>();
        services.AddScoped<AgentCatalogFacade>();
        services.AddScoped<IAgentCatalogFacade>(provider => provider.GetRequiredService<AgentCatalogFacade>());
        services.AddScoped<IAgentReferenceFacade>(provider => provider.GetRequiredService<AgentCatalogFacade>());
        services.AddScoped<IAgentProviderCompatibilityFacade>(provider =>
            provider.GetRequiredService<AgentCatalogFacade>()
        );
        services.AddScoped<AgentSuggestionAppService>();
        services.AddScoped<ExecutionPermissionService>();
        services.AddScoped<ExecutionInputService>();
        services.AddScoped<McpToolServerAppService>();
        return services;
    }
}
