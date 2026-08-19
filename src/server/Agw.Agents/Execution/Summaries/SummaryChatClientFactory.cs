using System.ClientModel;
using Agw.Agents.Definitions.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Agw.Agents.Execution.Summaries;

public sealed class SummaryChatClientFactory : ISummaryChatClientFactory
{
    private readonly AgentAppService _agentAppService;
    private readonly ILogger<SummaryChatClientFactory> _logger;

    public SummaryChatClientFactory(AgentAppService agentAppService, ILogger<SummaryChatClientFactory> logger)
    {
        _agentAppService = agentAppService;
        _logger = logger;
    }

    public async Task<IChatClient?> CreateAsync(Guid modelProviderId, CancellationToken cancellationToken = default)
    {
        var configuration = await _agentAppService
            .GetModelRuntimeConfigurationAsync(modelProviderId)
            .ConfigureAwait(false);
        if (configuration == null)
        {
            return null;
        }

        var authConfigs = configuration.Provider.AuthConfigs.Where(config => config.Enable).ToList();
        if (authConfigs.Count == 0)
        {
            _logger.LogError("No auth config for provider {ProviderName}.", configuration.Provider.Name);
            return null;
        }

        var authConfig = authConfigs[Random.Shared.Next(authConfigs.Count)];
        return configuration.Provider.ProviderType switch
        {
            ProviderType.OpenAIChatCompletions => CreateOpenAiClient(configuration, authConfig),
            ProviderType.Anthropic => CreateAnthropicClient(configuration, authConfig),
            _ => throw new AgwException(
                ErrorCodes.UnsupportedProviderType,
                $"Provider type '{configuration.Provider.ProviderType}' is not supported"
            ),
        };
    }

    private static IChatClient CreateOpenAiClient(
        AgentModelRuntimeConfiguration configuration,
        ProviderAuthConfig authConfig
    )
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(ResolveApiKey(authConfig)),
            new OpenAIClientOptions { Endpoint = new Uri(configuration.Provider.Endpoint) }
        );
        return client.GetChatClient(configuration.Model.Name).AsIChatClient();
    }

    private static IChatClient CreateAnthropicClient(
        AgentModelRuntimeConfiguration configuration,
        ProviderAuthConfig authConfig
    )
    {
        var client = new AnthropicClient(
            new ClientOptions { ApiKey = ResolveApiKey(authConfig), BaseUrl = configuration.Provider.Endpoint }
        );
        return client.AsIChatClient(configuration.Model.Name);
    }

    private static string ResolveApiKey(ProviderAuthConfig authConfig)
    {
        return authConfig.ApiKey!;
    }
}
