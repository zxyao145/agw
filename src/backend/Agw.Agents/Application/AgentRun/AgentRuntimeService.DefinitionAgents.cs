using System.ClientModel;

using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Exceptions;

using Anthropic;
using Anthropic.Core;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OpenAI;
using OpenAI.Chat;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    private async Task<AIAgent?> CreateDefinitionAgentAsync(
        Agent agentDefinition,
        Project project,
        CancellationToken cancellationToken)
    {
        if (!agentDefinition.ModelProviderId.HasValue)
        {
            return null;
        }

        var runtimeConfiguration =
            await _agentAppService.GetModelRuntimeConfigurationAsync(agentDefinition.ModelProviderId.Value);
        if (runtimeConfiguration == null)
        {
            return null;
        }

        var model = runtimeConfiguration.Model;
        var provider = runtimeConfiguration.Provider;
        var authConfigs = provider.AuthConfigs.Where(x => x.Enable).ToList();
        if (authConfigs.Count == 0)
        {
            _logger.LogError("no auth config for provider:{ProviderName}", provider.Name);
            return null;
        }

        var authConfig = authConfigs[Random.Shared.Next(authConfigs.Count)];
        IList<AITool>? tools =
            await CreateAgentTools(agentDefinition, project.Id, cancellationToken).ConfigureAwait(false);
        var skillsProvider = await CreateSkillsProviderAsync(agentDefinition.Id).ConfigureAwait(false);

        string workspace = project.GetMustWorkspace();
        AIAgent aiAgent = provider.ProviderType switch
        {
            ProviderType.OpenAI => CreateOpenAiAgent(agentDefinition, model, provider, authConfig, tools,
                skillsProvider, workspace),
            ProviderType.Anthropic => CreateAnthropicAgent(agentDefinition, model, provider, authConfig, tools,
                skillsProvider, workspace),
            _ => throw new AgwException(ErrorCodes.UnsupportedProviderType,
                $"Provider type '{provider.ProviderType}' is not supported")
        };

        aiAgent = aiAgent.AsBuilder()
            .Use(
                runFunc: _loggingMiddleware.LogRunMiddleware,
                runStreamingFunc: _loggingMiddleware.LogStreamingMiddleware)
            .Build();
        return aiAgent;
    }

    private AIAgent CreateOpenAiAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var apiKey = ResolveApiKey(authConfig);
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
        };
        var client = new OpenAIClient(credential, options);
        var chatCompletionClient = client.GetChatClient(model.Name);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return chatCompletionClient.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private AIAgent CreateAnthropicAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var anthropicClientOptions = new ClientOptions
        {
            ApiKey = ResolveApiKey(authConfig),
            BaseUrl = provider.Endpoint
        };
        var client = new AnthropicClient(anthropicClientOptions);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return client.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private string ResolveApiKey(ProviderAuthConfig authConfig)
    {
        if (authConfig.AuthType == ProviderAuthType.ApiKey)
        {
            return authConfig.ApiKey!;
        }

        var envVariableName = authConfig.EnvName!;
        var apiKeyFromEnv = Environment.GetEnvironmentVariable(envVariableName);
        if (string.IsNullOrWhiteSpace(apiKeyFromEnv))
        {
            _logger.LogError("Environment variable '{EnvName}' is not set or empty.", envVariableName);
            throw new AgwException(ErrorCodes.EnvironmentVariableNotSet,
                $"Environment variable '{envVariableName}' is not set or empty.");
        }

        return apiKeyFromEnv;
    }
}
