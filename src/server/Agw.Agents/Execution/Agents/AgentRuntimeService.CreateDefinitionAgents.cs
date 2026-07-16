using System.ClientModel;

using Agw.Agents.Execution.Agents.Utils;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

using Anthropic;
using Anthropic.Core;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OpenAI;
using OpenAI.Chat;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    private async Task<AIAgent?> CreateDefinitionAgentAsync(
        Agent agentDefinition,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
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
        var capabilities = await _capabilityComposer
            .ComposeAsync(agentDefinition, project, environmentVariables, cancellationToken)
            .ConfigureAwait(false);
        AIAgent? aiAgent = null;
        try
        {
            IList<AITool>? tools = capabilities.Tools.Count > 0
                ? capabilities.Tools.ToList()
                : null;
            var skillsProvider = await CreateSkillsProviderAsync(
                    agentDefinition,
                    project,
                    capabilities.PluginSkills)
                .ConfigureAwait(false);

            string workspace = project.GetMustWorkspace();
            aiAgent = provider.ProviderType switch
            {
                ProviderType.OpenAIChatCompletions => CreateOpenAiAgent(
                    agentDefinition,
                    model,
                    provider,
                    authConfig,
                    tools,
                    skillsProvider,
                    workspace),
                ProviderType.Anthropic => CreateAnthropicAgent(
                    agentDefinition,
                    model,
                    provider,
                    authConfig,
                    tools,
                    skillsProvider,
                    workspace),
                _ => throw new AgwException(
                    ErrorCodes.UnsupportedProviderType,
                    $"Provider type '{provider.ProviderType}' is not supported")
            };

            aiAgent = aiAgent.AsBuilder()
                .UseToolApproval(new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule],
                })
                .Use(
                    runFunc: _observabilityMiddleware.LogRunMiddleware,
                    runStreamingFunc: _observabilityMiddleware.LogStreamingMiddleware)
                .Use(
                    runFunc: _usageTrackingMiddleware.TrackRunMiddleware,
                    runStreamingFunc: _usageTrackingMiddleware.TrackStreamingMiddleware)
                .Build();
            return new ResourceOwningAIAgent(aiAgent, capabilities);
        }
        catch
        {
            if (aiAgent != null)
            {
                await DisposeAgentWithoutThrowingAsync(aiAgent).ConfigureAwait(false);
            }

            await DisposeResourceWithoutThrowingAsync(capabilities).ConfigureAwait(false);
            throw;
        }
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
        return authConfig.ApiKey!;
    }

    private static async ValueTask DisposeAgentWithoutThrowingAsync(AIAgent agent)
    {
        try
        {
            switch (agent)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch
        {
        }
    }

    private static async ValueTask DisposeResourceWithoutThrowingAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
